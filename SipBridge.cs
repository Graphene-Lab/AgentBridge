using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using AIOrchestrator;
using Microsoft.Extensions.Configuration;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Sys;
using SIPSorceryMedia.Abstractions;

// ═══════════════════════════════════════════════════════════════════════
//  SipBridge — SIP telephony for AgentBridge (see docs/sip.md)
//
//  Turns the server into a phone endpoint:
//    - incoming calls are auto-answered; the caller proves their identity with a
//      DTMF PIN (5 digits, max N attempts, then a persisted 24 h lockout) or is
//      accepted straight away when the P-Asserted-Identity (trusted provider) is
//      on the allow-list;
//    - outgoing calls via /sip call <sip-uri>;
//    - while connected, the caller's speech (decoded from RTP G.711) is converted
//      to text with the AIOffice.VoiceAgent --transcribe subprocess and fed to the
//      SHARED agentic voice conversation (AIOrchestrator ExecuteActionStream with
//      isVoiceChat — see VoiceConversation): same tools, same concise "speakable"
//      prompt, same markdown/emoji stripping and sentence splitting as the AIOffice
//      Voice panel. The agent replies are spoken back through the in-process Kokoro
//      TTS. The PIN gate is the shared PinAuthGate (AIOrchestrator), wired here.
//
//  Media: only G.711 (PCMU/PCMA) is offered — universal on every SIP endpoint and
//  decodable byte-per-sample with the bundled codecs. RTP ports are configurable
//  (RtpStartPort/RtpEndPort) for firewall deployments. One call at a time.
// ═══════════════════════════════════════════════════════════════════════

/// <summary>SIP telephony bridge: auto-answer + PIN gateway, outgoing calls, speech→agent→TTS loop.</summary>
public static class SipBridge
{
    /// <summary>Appsettings "Sip" section (see appsettings.json and docs/sip.md).</summary>
    public sealed class SipConfig
    {
        /// <summary>Master switch — the SIP server binds only when true.</summary>
        public bool Enabled { get; set; }
        /// <summary>UDP port for the SIP signalling (default 5060).</summary>
        public int ListenPort { get; set; } = 5060;
        /// <summary>Optional registrar/trunk (e.g. "sip:provider.example:5060"). When set the
        /// server REGISTERs (Username/Password) and receives/places calls through the provider.</summary>
        public string Registrar { get; set; } = "";
        /// <summary>Username and Password for REGISTER and authenticated calls.</summary>
        public string Username { get; set; } = "";
        /// <summary>See <see cref="Username"/>.</summary>
        public string Password { get; set; } = "";
        /// <summary>Incoming-call gate: "pin" (default), "allowlist" (P-Asserted-Identity) or "none".</summary>
        public string AnswerMode { get; set; } = "pin";
        /// <summary>The 5-digit DTMF PIN.</summary>
        public string Pin { get; set; } = "";
        /// <summary>Wrong-PIN attempts before the 24 h lockout (default 3).</summary>
        public int MaxPinAttempts { get; set; } = 3;
        /// <summary>Lockout duration after the attempts are exhausted (hours, default 24).</summary>
        public int LockoutHours { get; set; } = 24;
        /// <summary>Trusted caller URIs (P-Asserted-Identity from an authenticating provider/trunk)
        /// that skip the PIN. Matched on the full URI or on the user part alone.
        /// SECURITY: PAI is honored only when the INVITE comes from <see cref="Registrar"/> — in
        /// direct SIP (no registrar) the From header is client-controlled and must never grant
        /// access; use the allow-list only behind an authenticating trunk.</summary>
        public List<string> AllowedCallers { get; set; } = new();
        /// <summary>Agent set used for the conversation ("default-agent", "multi-agent", ...).</summary>
        public string Agent { get; set; } = "default-agent";
        /// <summary>Two-letter ISO language for STT/TTS (default: system language).</summary>
        public string Lang { get; set; } = "";
        /// <summary>Path to the AIOffice.VoiceAgent executable (--transcribe). Default:
        /// &lt;server dir&gt;\voiceagent-stt\AIOffice.VoiceAgent.exe.</summary>
        public string SttExePath { get; set; } = "";
        /// <summary>Optional fixed RTP port range ("start-end", e.g. 40000-41000) for firewalled
        /// deployments; empty = ephemeral ports.</summary>
        public string RtpPortRange { get; set; } = "";
    }

    /// <summary>Lifecycle phase of the active call, reported by /v1/sip/status.</summary>
    public enum CallPhase
    {
        /// <summary>No active call.</summary>
        Idle,
        /// <summary>Outgoing call ringing.</summary>
        Ringing,
        /// <summary>Waiting for the DTMF PIN.</summary>
        Pin,
        /// <summary>Connected to the agent (speech loop active).</summary>
        Conversation,
        /// <summary>Call being torn down.</summary>
        Ended
    }

    private sealed class CallContext
    {
        public required VoIPMediaSession Media;
        public SIPServerUserAgent? Uas;
        public required string RemoteUri;
        public string? CallId;                  // SIP Call-ID of the INVITE that started the call
        public volatile CallPhase Phase = CallPhase.Pin;
        public ActiveSession? AgentSession;
        public SipVoiceMedia? VoiceMedia;       // IAudioMedia endpoint of this call
        public CancellationTokenSource Cts = new();
        public Task? Loop;
        public volatile bool Validating;
    }

    /// <summary>
    /// The SIP media endpoint: implements <see cref="IAudioMedia"/> so the WHOLE conversation is
    /// driven by <see cref="VoiceConversation.RunConversationAsync"/> — the medium is a mere I/O
    /// channel: RTP audio in (G.711 → VAD → whisper → <see cref="SpeechReceived"/>) and Kokoro TTS
    /// out (raw PCM → RTP). No conversation logic lives here (see AIOrchestrator/ARCHITECTURE.md).
    /// </summary>
    private sealed class SipVoiceMedia : IAudioMedia
    {
        private readonly CallContext _call;
        private readonly UtteranceDetector _vad = new();
        private readonly SemaphoreSlim _transcribeGate = new(1, 1);
        private volatile bool _speaking;
        private volatile bool _conversationActive;   // set by StartAsync: distinguishes conversation replies from pre-PIN announcements

        public SipVoiceMedia(CallContext call) => _call = call;

        public string Language => VoiceConversation.ResolveLang(Cfg.Lang);

        public event Action<string>? SpeechReceived;

        public Task StartAsync(CancellationToken ct = default)
        {
            _call.Media.OnRtpPacketReceived += OnRtpAudio;
            _vad.UtteranceReady += OnUtterance;
            _conversationActive = true;
            Log.LogStep("SIP RTP capture attached (conversation media loop started)");
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            _call.Media.OnRtpPacketReceived -= OnRtpAudio;
            _vad.UtteranceReady -= OnUtterance;
            _vad.Reset();
            _conversationActive = false;
            return Task.CompletedTask;
        }

        // Decodes G.711 payloads (1 byte = 1 sample @ 8 kHz) into 16-bit PCM and feeds the VAD.
        // RFC 4733 DTMF events (dynamic payload type) are skipped — they never reach the STT.
        // Capture is paused while the TTS reply plays (no barge-in: a hands-free caller would
        // otherwise echo the reply back into the STT).
        private void OnRtpAudio(IPEndPoint _, SDPMediaTypesEnum __, RTPPacket packet)
        {
            if (_call.Phase != CallPhase.Conversation || _speaking) return;
            var payload = packet.Payload;
            if (payload == null || payload.Length == 0) return;

            var pt = packet.Header.PayloadType;
            if (pt != PcmuPayloadType && pt != PcmaPayloadType) return;

            var pcm = new byte[payload.Length * 2];
            for (int i = 0; i < payload.Length; i++)
            {
                var sample = pt == PcmuPayloadType
                    ? MuLawDecoder.MuLawToLinearSample(payload[i])
                    : ALawDecoder.ALawToLinearSample(payload[i]);
                pcm[i * 2] = (byte)(sample & 0xFF);
                pcm[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }
            _vad.Feed(pcm);
        }

        private void OnUtterance(byte[] pcm8k)
        {
            Log.LogStep($"SIP utterance detected ({pcm8k.Length} bytes)");
            _ = Task.Run(async () =>
            {
                try
                {
                    await _transcribeGate.WaitAsync();
                    try
                    {
                        var text = await TranscribeAsync(pcm8k, Language, _call.Cts.Token);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            Log.LogStep($"SIP caller said: {text}");
                            SpeechReceived?.Invoke(text);
                        }
                    }
                    finally
                    {
                        _transcribeGate.Release();
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Log.LogStep($"SIP STT error: {ex.Message}");
                }
            });
        }

        /// <summary>Renders speakable text to the caller: in-process Kokoro TTS → raw PCM → RTP.</summary>
        public async Task SpeakAsync(string text, bool isLast, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (!TtsEngine.IsAvailable || _call.Media.IsClosed) return;
            if (_conversationActive) Log.LogStep($"SIP agent replied: {text}");
            byte[] wav;
            try
            {
                wav = TtsEngine.Synthesize(text, null, null, Language);
            }
            catch (Exception ex)
            {
                Log.LogStep($"SIP TTS failed: {ex.Message}");
                return;
            }
            _speaking = true;
            try
            {
                // Strip the 44-byte RIFF header: SendAudioFromStream expects raw 16-bit PCM.
                var pcm = wav.AsSpan(44).ToArray();
                await _call.Media.AudioExtrasSource.SendAudioFromStream(new MemoryStream(pcm), AudioSamplingRatesEnum.Rate24kHz);
            }
            finally
            {
                _speaking = false;
            }
        }
    }

    /// <summary>Energy-based voice activity detection on 8 kHz PCM16, ported from the
    /// AIOffice.VoiceAgent WhisperRecognizer (adaptive noise floor + 700 ms silence hangover):
    /// collects a spoken utterance and raises <see cref="UtteranceReady"/> when it ends.</summary>
    private sealed class UtteranceDetector
    {
        private const int FrameSamples = 80;         // 10 ms @ 8 kHz
        private const int HangoverFrames = 70;       // 700 ms of silence ends an utterance
        private const int MinUtteranceMs = 350;
        private const int MaxUtteranceMs = 30_000;
        private const double MinThreshold = 0.006;

        private readonly MemoryStream _utterance = new();
        private DateTime _speechStart;
        private double _noiseFloor = 0.004;
        private int _hangover;

        public event Action<byte[]>? UtteranceReady;

        public void Feed(ReadOnlySpan<byte> pcm)
        {
            _utterance.Write(pcm);

            if (_utterance.Length > MaxUtteranceMs * 2 * 8)   // 8 kHz → 16 000 bytes/s
            {
                Flush();
                return;
            }

            var frameBytes = FrameSamples * 2;
            var end = (int)_utterance.Length;
            var start = end - pcm.Length;
            for (int offset = start; offset + frameBytes <= end; offset += frameBytes)
                UpdateVad(Rms(_utterance.GetBuffer().AsSpan(offset, frameBytes)));
        }

        public void Reset()
        {
            _utterance.SetLength(0);
            _hangover = 0;
            _speechStart = default;
        }

        private static double Rms(ReadOnlySpan<byte> frame)
        {
            long sumSquares = 0;
            for (int i = 0; i < frame.Length; i += 2)
            {
                var sample = (short)(frame[i] | frame[i + 1] << 8);
                sumSquares += (long)sample * sample;
            }
            return Math.Sqrt(sumSquares / (double)(frame.Length / 2)) / short.MaxValue;
        }

        private void UpdateVad(double rms)
        {
            var threshold = Math.Max(_noiseFloor * 3.5, MinThreshold);
            var now = DateTime.UtcNow;

            if (_hangover > 0)
            {
                if (rms > threshold)
                    _hangover = 0;
                else if (++_hangover >= HangoverFrames)
                    Flush();
            }
            else if (_speechStart != default)
            {
                if (rms <= threshold)
                    _hangover = 1;
                else if ((now - _speechStart).TotalMilliseconds > MaxUtteranceMs)
                    Flush();
            }
            else if (rms > threshold)
            {
                _speechStart = now;
            }
            else
            {
                _noiseFloor = Math.Min(_noiseFloor, Math.Max(rms, 0.0005));
                _noiseFloor = _noiseFloor * 0.995 + Math.Min(rms, threshold) * 0.005;
            }
        }

        private void Flush()
        {
            var duration = _speechStart == default ? 0 : (DateTime.UtcNow - _speechStart).TotalMilliseconds;
            var bytes = _utterance.ToArray();
            _utterance.SetLength(0);
            _hangover = 0;
            _speechStart = default;

            if (duration < MinUtteranceMs || bytes.Length < MinUtteranceMs * 2 * 8)
                return;
            UtteranceReady?.Invoke(bytes);
        }
    }

    private static readonly object Sync = new();
    private static readonly object ValidateLock = new();   // serializes the "start validation" check-and-set (OnDtmfTone vs the chained finally)
    private static readonly SemaphoreSlim CallGate = new(1, 1);
    private static SipConfig Cfg = new();
    private static string StartupProvider = "DeepSeekBridge";
    private static bool Anonymize;
    private static SIPTransport? Transport;
    private static SIPUserAgent? Ua;
    private static SIPRegistrationUserAgent? Registration;
    private static Timer? HealthTimer;
    private static bool AnswerEnabled = true;
    private static CallContext? Call;
    // PIN gate (AIOrchestrator): reusable across voice media; wired here for SIP DTMF. Lockout
    // persistence is delegated to SipBridge (sipstate.json).
    private static PinAuthGate Gate = new("", 3, TimeSpan.FromHours(24));
    private static readonly string StatePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agent", "sipstate.json");

    // G.711 static payload types (RFC 3551); RFC 4733 DTMF events use a dynamic type (usually 101).
    private const byte PcmuPayloadType = 0;
    private const byte PcmaPayloadType = 8;
    private const int MaxAgentIterations = 20;
    private const int RingTimeoutSeconds = 60;
    private const int RegistrationExpiry = 300;

    /// <summary>True when the SIP server is configured (appsettings Sip:Enabled).</summary>
    public static bool IsEnabled => Cfg.Enabled;

    /// <summary>True when the SIP signalling channel is bound.</summary>
    public static bool IsListening => Transport != null;

    /// <summary>Machine-readable status consumed by GET /v1/sip/status and the TUI.</summary>
    public static object Status
    {
        get
        {
            CallContext? call;
            lock (Sync) call = Call;
            var phase = call?.Phase ?? CallPhase.Idle;
            return new
            {
                enabled = Cfg.Enabled,
                listening = Transport != null,
                answer_enabled = AnswerEnabled,
                answer_mode = Cfg.AnswerMode,
                registered = Registration?.IsRegistered ?? false,
                call_active = call != null,
                phase = phase.ToString().ToLowerInvariant(),
                remote = call?.RemoteUri,
                pin_remaining = Gate.IsLocked ? 0 : Math.Max(0, Cfg.MaxPinAttempts - Gate.Attempts),
                locked_until = Gate.LockedUntilUtc,
                stt_available = ResolveSttExe() != null,
                tts_available = TtsEngine.IsAvailable,
                rtp_port_range = Cfg.RtpPortRange,
            };
        }
    }

    /// <summary>Loads the configuration and the persisted lockout state.</summary>
    public static void Init(IConfiguration config, string startupProvider, bool anonymize)
    {
        StartupProvider = startupProvider;
        Anonymize = anonymize;
        Cfg = config.GetSection("Sip").Get<SipConfig>() ?? new SipConfig();
        Cfg.Pin = (Cfg.Pin ?? "").Trim();
        Cfg.MaxPinAttempts = Math.Max(1, Cfg.MaxPinAttempts);
        Cfg.LockoutHours = Math.Max(1, Cfg.LockoutHours);
        Gate = new PinAuthGate(Cfg.Pin, Cfg.MaxPinAttempts, TimeSpan.FromHours(Cfg.LockoutHours));
        LoadLockout();
    }

    /// <summary>Starts the SIP signalling channel + REGISTER (when configured). Returns null on
    /// success, or the error message (e.g. port already in use).</summary>
    public static async Task<string?> StartAsync()
    {
        if (!Cfg.Enabled || Transport != null) return null;
        try
        {
            var transport = new SIPTransport();
            transport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, Cfg.ListenPort)));
            var ua = CreateUserAgent(transport);

            SIPRegistrationUserAgent? registration = null;
            if (!string.IsNullOrWhiteSpace(Cfg.Registrar) && !string.IsNullOrWhiteSpace(Cfg.Username))
            {
                registration = new SIPRegistrationUserAgent(transport, Cfg.Username, Cfg.Password,
                    Cfg.Registrar, RegistrationExpiry, exitOnUnequivocalFailure: false);
                registration.RegistrationFailed += (_, _, err) => Log.LogStep($"SIP registration failed: {err}");
                registration.RegistrationSuccessful += (_, _) => Log.LogStep("SIP registration successful");
                registration.Start();
            }

            Transport = transport;
            Ua = ua;
            Registration = registration;
            // Self-heal: the shared SIPUserAgent occasionally fails to clear its internal
            // dialog state after a server-initiated hangup, which would silently drop every
            // later INVITE. When no call is active but the agent still thinks one is, rebuild it.
            HealthTimer = new Timer(_ => EnsureUserAgentHealthy(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            if (Cfg.AnswerMode == "allowlist" && string.IsNullOrWhiteSpace(Cfg.Registrar))
                Log.LogStep("SIP warning: allow-list mode without a registrar — the caller identity (From header) is NOT authenticated and can be spoofed; run allow-list only behind an authenticating trunk");
            if (!TtsEngine.IsAvailable)
                Log.LogStep("SIP warning: TTS unavailable (Kokoro voices/onnxruntime missing) — announcements and agent replies will be silent for callers");
            Log.LogStep($"SIP listening on UDP {Cfg.ListenPort} (answer mode '{Cfg.AnswerMode}', agent '{Cfg.Agent}')");
            return null;
        }
        catch (Exception ex)
        {
            Log.LogStep($"SIP start failed: {ex.Message}");
            try { Transport?.Shutdown(); } catch { }
            Transport = null;
            Ua = null;
            return ex.Message;
        }
    }

    /// <summary>Stops the SIP server (unregisters + closes the channel).</summary>
    public static void Stop()
    {
        HealthTimer?.Dispose();
        HealthTimer = null;
        try { Registration?.Stop(); } catch { }
        Registration = null;
        try { Transport?.Shutdown(); } catch { }
        Transport = null;
        Ua = null;
        EndCall("shutdown");
    }

    private static SIPUserAgent CreateUserAgent(SIPTransport transport)
    {
        var ua = new SIPUserAgent(transport, null, false);
        ua.OnIncomingCall += OnIncomingCall;
        ua.OnDtmfTone += OnDtmfTone;
        ua.OnCallHungup += _ => EndCall("remote-hangup");
        ua.ServerCallCancelled += (_, _) => EndCall("remote-cancel");
        // While a call is active the SIPUserAgent silently drops new INVITEs (its handler only
        // delivers them when no dialog is set): answer those at the transport level with 486,
        // so the second caller gets a real Busy response instead of a ring timeout.
        transport.SIPTransportRequestReceived += OnTransportRequest;
        return ua;
    }

    private static void EnsureUserAgentHealthy()
    {
        var transport = Transport;
        var ua = Ua;
        if (transport == null || ua == null) return;
        CallContext? call;
        lock (Sync) call = Call;
        if (call == null)
        {
            if (!ua.IsCallActive) return;
            Log.LogStep("SIP user agent cleanup failed — rebuilding it (stale dialog would drop new INVITEs)");
            try { ua.Close(); } catch { }
            Ua = CreateUserAgent(transport);
            return;
        }
        // We still hold a call the user agent reports as inactive: the remote hangup reached
        // the transport but OnCallHungup was missed. The stale internal dialog would silently
        // drop every later INVITE — clear the orphan and rebuild now, instead of waiting for
        // the next INVITE to trip the transport-level handler.
        if (!ua.IsCallActive)
        {
            Log.LogStep("SIP orphaned call state cleared by watchdog (user agent has no active dialog)");
            lock (Sync) if (Call == call) Call = null;
            try { ua.Close(); } catch { }
            Ua = CreateUserAgent(transport);
        }
    }

    private static async Task OnTransportRequest(SIPEndPoint _, SIPEndPoint __, SIPRequest request)
    {
        if (request.Method != SIPMethodsEnum.INVITE) return;
        CallContext? call;
        lock (Sync) call = Call;
        if (call == null) return;   // no active call — let the user agent answer this INVITE
        if (call.CallId != null && request.Header.CallId == call.CallId) return;   // same dialog (INVITE retransmission) — the user agent handles it
        // A call is active and this is a NEW call (different CallId, or an outgoing call whose
        // CallId is never set): reject it below — after the orphan-cleanup check.

        // The shared user agent no longer tracks any dialog while we still hold a call: the
        // remote hangup was processed at the transport but the cleanup event was missed. Clear
        // the orphaned call state and rebuild the user agent — its stale internal dialog would
        // silently drop this INVITE and every later one.
        if (Ua != null && !Ua.IsCallActive)
        {
            lock (Sync)
            {
                if (Call == call) Call = null;
                var transport = Transport;
                if (transport == null || Ua == null) return;
                try { Ua.Close(); } catch { }
                Ua = CreateUserAgent(transport);
            }
            Log.LogStep("SIP orphaned call state cleared (user agent has no active dialog)");
            return;
        }

        try
        {
            var busy = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.BusyHere, null);
            await Transport!.SendResponseAsync(busy);
            Log.LogStep("SIP call rejected (486 Busy) at the transport — a call is already active");
        }
        catch (Exception ex)
        {
            Log.LogStep($"SIP busy response failed: {ex.Message}");
        }
    }

    /// <summary>Enables/disables the incoming-call auto-answer gate.</summary>
    public static void SetAnswerEnabled(bool on) => AnswerEnabled = on;

    /// <summary>Places an outgoing call to a SIP URI (or a bare number routed via the registrar).
    /// Blocks until the call is answered or fails (ring timeout). Returns an error message, or
    /// null on success.</summary>
    public static async Task<string?> CallAsync(string dst)
    {
        if (!Cfg.Enabled) return "SIP not enabled (Sip:Enabled)";
        var uri = NormalizeDestination(dst);
        if (uri == null) return $"Invalid SIP destination: {dst} — use a full URI like sip:user@host";
        if (!await CallGate.WaitAsync(0)) return "A SIP call is already active";

        try
        {
            lock (Sync) if (Call != null) return "A SIP call is already active";

            var media = CreateMedia();
            var call = new CallContext { Media = media, RemoteUri = uri, Phase = CallPhase.Ringing };
            lock (Sync) Call = call;

            var answeredOk = false;
            void OnAnswered(ISIPClientUserAgent _, SIPResponse resp)
            {
                if (resp.Status == SIPResponseStatusCodesEnum.Ok) answeredOk = true;
            }
            void OnFailed(ISIPClientUserAgent _, string err, SIPResponse __) =>
                Log.LogStep($"SIP outbound call failed: {err}");

            Ua!.ClientCallAnswered += OnAnswered;
            Ua.ClientCallFailed += OnFailed;
            try
            {
                var ok = await Ua.Call(uri, Cfg.Username, Cfg.Password, media, ringTimeout: RingTimeoutSeconds);
                if (!ok || !answeredOk)
                {
                    EndCall("outbound-failed");
                    return "Call failed or not answered";
                }
            }
            finally
            {
                Ua.ClientCallAnswered -= OnAnswered;
                Ua.ClientCallFailed -= OnFailed;
            }

            call.Phase = CallPhase.Conversation;
            StartConversation(call);
            Log.LogStep($"SIP outbound call answered: {uri}");
            return null;
        }
        finally
        {
            CallGate.Release();
        }
    }

    /// <summary>Hangs up the active call (incoming or outgoing).</summary>
    public static void Hangup()
    {
        lock (Sync) if (Call == null) return;
        EndCall("hangup");
        try { Ua?.Hangup(); } catch { }
    }

    // ─── Incoming call flow ─────────────────────────────────────────────

    private static async void OnIncomingCall(SIPUserAgent ua, SIPRequest req)
    {
        try
        {
            if (!AnswerEnabled || !await CallGate.WaitAsync(0))
            {
                Reject(ua, req, SIPResponseStatusCodesEnum.BusyHere);
                return;
            }

            var caller = CallerIdentity(req);
            CallContext? call;
            try
            {
                lock (Sync) if (Call != null) { Reject(ua, req, SIPResponseStatusCodesEnum.BusyHere); return; }

                var media = CreateMedia();
                var uas = ua.AcceptCall(req);
                call = new CallContext { Media = media, Uas = uas, RemoteUri = caller, Phase = CallPhase.Pin, CallId = req.Header.CallId };
                call.VoiceMedia = new SipVoiceMedia(call);   // used for the announcements too
                lock (Sync) Call = call;
                await ua.Answer(uas, media);
                Log.LogStep($"SIP incoming call answered from {caller}");
            }
            finally
            {
                // The gate guards call SETUP only: the spoken announcements below must not hold
                // it, or a caller hanging up mid-announcement would block every new call until
                // the (possibly long) TTS await completes. The Call != null check and the
                // transport-level 486 handler keep the one-call-at-a-time rule afterwards.
                CallGate.Release();
            }

            // Locked out (PIN attempts exhausted in the last 24 h): answer with a spoken
            // explanation, then hang up — clearer for the legit user than a bare SIP error.
            if (Gate.IsLocked)
            {
                Log.LogStep("SIP locked-out call answered — playing notice");
                await call.VoiceMedia!.SpeakAsync(AnnounceLocked(), true, default);
                EndCall("locked-out");
                Log.LogStep("SIP locked-out notice played — hanging up");
                try { Ua?.Hangup(); } catch (Exception ex) { Log.LogStep($"SIP locked-out hangup failed: {ex.Message}"); }
                EnsureUserAgentHealthy();
                return;
            }

            if (Cfg.AnswerMode == "none" || (Cfg.AnswerMode == "allowlist" && IsAllowed(caller)))
            {
                call.Phase = CallPhase.Conversation;
                StartConversation(call);
            }
            else if (!string.IsNullOrEmpty(Cfg.Pin))
            {
                Gate.ResetBuffer();   // fresh digit buffer; wrong attempts stay cumulative across calls (lockout after MaxPinAttempts total)
                await call.VoiceMedia!.SpeakAsync(AnnounceWelcome(), true, default);
            }
            else
            {
                Log.LogStep("SIP call rejected: no PIN configured and caller not allowed");
                EndCall("not-authorized");
                try { Ua?.Hangup(); } catch { }   // server-originated BYE + SIPUserAgent cleanup
                EnsureUserAgentHealthy();
            }
        }
        catch (Exception ex)
        {
            Log.LogStep($"SIP incoming call failed: {ex.Message}");
            try { EndCall("error"); } catch { }
        }
    }

    private static void Reject(SIPUserAgent ua, SIPRequest req, SIPResponseStatusCodesEnum status)
    {
        Log.LogStep($"SIP call rejected ({status}) from {CallerIdentity(req)}");
        try
        {
            var uas = ua.AcceptCall(req);
            uas.Reject(status, null);
        }
        catch (Exception ex)
        {
            Log.LogStep($"SIP reject failed: {ex.Message}");
        }
    }

    /// <summary>Identity of the caller. P-Asserted-Identity (RFC 3325) is trusted ONLY when the
    /// INVITE actually came from the configured registrar/trunk — the header is inserted by an
    /// authenticating proxy in the path. In direct SIP there is no such proxy: any caller can put
    /// any URI in the header, so it is ignored and the client-controlled From header is returned
    /// instead (never trusted for the allow-list without a trunk).</summary>
    private static string CallerIdentity(SIPRequest req)
    {
        if (IsFromRegistrar(req.RemoteSIPEndPoint))
        {
            var paid = req.Header.PassertedIdentity.FirstOrDefault()?.URI?.ToString();
            if (!string.IsNullOrWhiteSpace(paid)) return paid;
        }
        return req.Header.From?.FromURI?.ToString() ?? req.RemoteSIPEndPoint?.ToString() ?? "";
    }

    /// <summary>True when the request's source address belongs to the configured registrar/trunk
    /// (hostname match, IP-literal match, or DNS resolution of the hostname).</summary>
    private static bool IsFromRegistrar(SIPEndPoint? remote)
    {
        if (remote?.Address == null || string.IsNullOrWhiteSpace(Cfg.Registrar)) return false;
        var host = RegistrarHost();
        if (host == null) return false;
        var src = remote.Address.ToString();   // SIPSorcery's IPAddress: ToString() is the dotted quad
        if (string.Equals(src, host, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) && (src == "127.0.0.1" || src == "::1")) return true;
        try
        {
            return Dns.GetHostAddresses(host).Any(ip => string.Equals(ip.ToString(), src, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAllowed(string? caller)
    {
        if (string.IsNullOrWhiteSpace(caller)) return false;
        if (SIPURI.TryParse(caller, out var uri) && !string.IsNullOrEmpty(uri.User))
        {
            foreach (var entry in Cfg.AllowedCallers)
            {
                var a = (entry ?? "").Trim();
                if (a.Length == 0) continue;
                if (caller.Equals(a, StringComparison.OrdinalIgnoreCase)) return true;
                if (!a.Contains('@') && uri.User.Equals(a, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    // ─── DTMF PIN ───────────────────────────────────────────────────────

    private static void OnDtmfTone(byte tone, int durationMs)
    {
        CallContext? call;
        lock (Sync) call = Call;
        if (call == null || call.Phase != CallPhase.Pin || tone > 9) return;

        // Submit + validation start are atomic (ValidateLock): a DTMF arriving while the
        // previous attempt's finally is between "clear Validating" and "re-check the buffer"
        // cannot start a second concurrent validation (double announcement / double hangup).
        lock (ValidateLock)
        {
            Gate.SubmitDigit((char)('0' + tone));
            // One validation at a time: digits that arrive while the previous attempt is being
            // announced stay in the gate buffer and trigger the next validation when it completes.
            if (!call.Validating && Gate.TrySubmitPending(out var result))
            {
                call.Validating = true;
                _ = Task.Run(() => ValidatePinAsync(call, result));
            }
        }
    }

    private static async Task ValidatePinAsync(CallContext call, PinCheckResult result)
    {
        try
        {
            switch (result)
            {
                case PinCheckResult.Accepted:
                    call.Phase = CallPhase.Conversation;
                    await call.VoiceMedia!.SpeakAsync(AnnounceWelcomeOk(), true, default);
                    Log.LogStep("SIP PIN accepted — starting conversation");
                    StartConversation(call);
                    return;

                case PinCheckResult.Locked:
                    SaveLockout();
                    call.Phase = CallPhase.Ended;
                    Log.LogStep("SIP PIN attempts exhausted — locked");
                    await call.VoiceMedia!.SpeakAsync(AnnounceLocked(), true, default);
                    // The SIPUserAgent-level Hangup (not the raw UAS one) also clears the internal
                    // m_uas/m_sipDialogue state: a bare UAS hangup leaves them stale and every later
                    // incoming INVITE is silently ignored by the shared user agent. Runs BEFORE
                    // EndCall so the media session is still live while the BYE is generated.
                    try { Ua?.Hangup(); }
                    catch (Exception ex) { Log.LogStep($"SIP pin-locked hangup failed: {ex.Message}"); }
                    EndCall("pin-locked");
                    EnsureUserAgentHealthy();
                    return;

                default:   // Wrong
                    var remaining = Math.Max(0, Cfg.MaxPinAttempts - Gate.Attempts);
                    Log.LogStep($"SIP wrong PIN — {remaining} attempts left");
                    await call.VoiceMedia!.SpeakAsync(AnnouncePinWrong(remaining), true, default);
                    return;
            }
        }
        finally
        {
            // Digits that arrived while the announcement was playing may complete the next
            // attempt: chain the validation until the buffer runs short of a full PIN. The
            // whole clear-and-recheck is atomic with OnDtmfTone's submit (ValidateLock), so
            // no two validations can run concurrently.
            lock (ValidateLock)
            {
                call.Validating = false;
                if (call.Phase == CallPhase.Pin && Gate.TrySubmitPending(out var next))
                {
                    call.Validating = true;
                    _ = Task.Run(() => ValidatePinAsync(call, next));
                }
            }
        }
    }

    private static void LoadLockout()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
            if (doc.RootElement.TryGetProperty("locked_until", out var v) &&
                DateTime.TryParse(v.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var lu))
                Gate.RestoreLockout(lu);
        }
        catch (Exception ex)
        {
            Log.LogStep($"SIP lockout state unreadable: {ex.Message}");
        }
    }

    private static void SaveLockout()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(new { locked_until = Gate.LockedUntilUtc?.ToString("O") }));
        }
        catch (Exception ex)
        {
            Log.LogStep($"SIP lockout state not persisted: {ex.Message}");
        }
    }

    // ─── Conversation: the shared media loop (IAudioMedia) ───────────────

    private static void StartConversation(CallContext call)
    {
        Log.LogStep("SIP conversation started (media loop launching)");
        call.Phase = CallPhase.Conversation;
        call.AgentSession = SessionStore.Create(StartupProvider, Anonymize);
        // Background-task completions are spoken to the caller (agent initiative turns).
        call.AgentSession.Orchestrator.AgentProgress += OnAgentProgress;
        // The whole conversation (RTP speech in → whisper → agent → Kokoro → RTP out) runs in the
        // SHARED media loop: the medium (SipVoiceMedia) is a mere I/O channel, the logic lives in
        // VoiceConversation.RunConversationAsync (see AIOrchestrator/ARCHITECTURE.md → "Media as I/O").
        call.VoiceMedia ??= new SipVoiceMedia(call);
        call.Loop = Task.Run(() => VoiceConversation.RunConversationAsync(
            call.VoiceMedia, call.AgentSession.Orchestrator, AgentTools.Resolve(Cfg.Agent),
            maxIterations: MaxAgentIterations, ct: call.Cts.Token));
    }

    private static void OnAgentProgress(object? _, AgentHarness.AgentProgressEventArgs e)
    {
        if (e.State != AgentHarness.AgentState.Initiative || string.IsNullOrWhiteSpace(e.Message)) return;
        CallContext? call;
        lock (Sync) call = Call;
        var media = call?.VoiceMedia;
        if (media == null) return;
        _ = Task.Run(() => media.SpeakAsync(e.Message, false, call!.Cts.Token));
    }

    // ─── STT: AIOffice.VoiceAgent --transcribe subprocess ───────────────

    private static async Task<string?> TranscribeAsync(byte[] pcm8k, string lang, CancellationToken ct)
    {
        var exe = ResolveSttExe();
        if (exe == null)
        {
            Log.LogStep("SIP STT unavailable: AIOffice.VoiceAgent not found (voiceagent-stt/)");
            return null;
        }

        var tmp = Path.Combine(Path.GetTempPath(), $"sip-{Guid.NewGuid():N}.wav");
        try
        {
            WriteWav8k(tmp, pcm8k);
            var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            psi.ArgumentList.Add("--transcribe");
            psi.ArgumentList.Add(tmp);
            if (!string.IsNullOrWhiteSpace(lang)) { psi.ArgumentList.Add("--lang"); psi.ArgumentList.Add(lang); }

            using var proc = Process.Start(psi);
            if (proc == null) return null;

            try
            {
                string? text = null;
                while (await proc.StandardOutput.ReadLineAsync(ct) is { } line)
                    if (TryParseTranscript(line, out var t)) text = t;

                await proc.WaitForExitAsync(ct);
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }
            catch (OperationCanceledException)
            {
                // The call ended while the subprocess was transcribing: kill it now, or orphaned
                // STT processes (and their temp WAVs, which they may still have open) accumulate.
                try { proc.Kill(true); } catch { }
                return null;
            }
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Log.LogStep($"SIP STT failed: {ex.Message}");
            return null;
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    private static string? ResolveSttExe()
    {
        if (!string.IsNullOrWhiteSpace(Cfg.SttExePath))
        {
            var p = Path.IsPathRooted(Cfg.SttExePath)
                ? Cfg.SttExePath
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Cfg.SttExePath);
            if (File.Exists(p)) return p;
        }
        var exeName = OperatingSystem.IsWindows() ? "AIOffice.VoiceAgent.exe" : "AIOffice.VoiceAgent";
        var def = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "voiceagent-stt", exeName);
        return File.Exists(def) ? def : null;
    }

    private static bool TryParseTranscript(string line, out string text)
    {
        text = "";
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var type) && type.GetString() == "transcript" &&
                root.TryGetProperty("text", out var t))
                text = t.GetString() ?? "";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void WriteWav8k(string path, byte[] pcm)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        const int sampleRate = 8000;
        w.Write("RIFF"u8);
        w.Write(36 + pcm.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(sampleRate);
        w.Write(sampleRate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(pcm.Length);
        w.Write(pcm);
    }

    // ─── Media session ──────────────────────────────────────────────────

    private static VoIPMediaSession CreateMedia()
    {
        // The config ctor (NOT the parameterless one — that enables the on-hold music
        // generator, which would stream music to the caller for the whole call).
        var endpoint = new MediaEndPoints { AudioSource = new AudioExtrasSource() };
        var config = new VoIPMediaSessionConfig { MediaEndPoint = endpoint };
        if (TryParsePortRange(Cfg.RtpPortRange, out var start, out var end))
            config.RtpPortRange = new PortRange(start, end);
        var media = new VoIPMediaSession(config);
        endpoint.AudioSource!.RestrictFormats(f => f.Codec == AudioCodecsEnum.PCMU || f.Codec == AudioCodecsEnum.PCMA);
        media.AcceptRtpFromAny = true;
        return media;
    }

    private static bool TryParsePortRange(string range, out int start, out int end)
    {
        start = 0;
        end = 0;
        if (string.IsNullOrWhiteSpace(range)) return false;
        var parts = range.Split('-', 2);
        return int.TryParse(parts[0].Trim(), out start) &&
               int.TryParse(parts[1].Trim(), out end) &&
               start > 0 && end >= start;
    }

    // ─── Helpers ────────────────────────────────────────────────────────

    /// <summary>Accepts a full SIP URI ("sip:user@host") or a bare number/extension routed
    /// through the configured registrar. Returns null when unusable.</summary>
    private static string? NormalizeDestination(string dst)
    {
        var s = dst.Trim();
        if (SIPURI.TryParse(s, out _)) return s;
        if (string.IsNullOrWhiteSpace(Cfg.Registrar)) return null;
        if (s.Contains('@') || s.Contains(':')) return null;
        var host = RegistrarHost();
        return host == null ? null : $"sip:{s}@{host}";
    }

    private static string? RegistrarHost()
    {
        if (SIPURI.TryParse(Cfg.Registrar, out var uri)) return uri.Host;
        var s = Cfg.Registrar.Trim();
        if (s.StartsWith("sip:", StringComparison.OrdinalIgnoreCase)) s = s[4..];
        return s.Length > 0 ? s : null;
    }

    private static void EndCall(string reason)
    {
        CallContext? call;
        lock (Sync)
        {
            call = Call;
            Call = null;
        }
        if (call == null) return;
        Log.LogStep($"SIP call ended ({reason})");
        if (call.AgentSession != null)
            call.AgentSession.Orchestrator.AgentProgress -= OnAgentProgress;
        // Cancelling the token ends the shared media loop (RunConversationAsync → StopAsync),
        // which detaches the RTP capture and resets the VAD.
        call.Cts.Cancel();
        try { call.Media.AudioExtrasSource.CancelSendAudioFromStream(); } catch { }
        try { call.Media.Close(null); } catch { }
    }

    // ─── Announcements (language follows the shared VoiceConversation resolution) ──

    private static bool Italian => VoiceConversation.ResolveLang(Cfg.Lang).StartsWith("it", StringComparison.OrdinalIgnoreCase);

    private static string AnnounceWelcome() =>
        Italian ? "Benvenuto. Inserisci il codice PIN di cinque cifre." : "Welcome. Please enter the five digit PIN code.";

    private static string AnnounceWelcomeOk() =>
        Italian ? "Codice corretto. Collegamento con l'agente in corso." : "Code accepted. Connecting you to the agent.";

    private static string AnnouncePinWrong(int remaining) =>
        Italian ? $"Codice errato. Tentativi rimasti: {remaining}." : $"Wrong code. Attempts left: {remaining}.";

    private static string AnnounceLocked() =>
        Italian ? "Tentativi esauriti. Il servizio è bloccato per ventiquattro ore." : "Too many attempts. The service is locked for twenty four hours.";
}
