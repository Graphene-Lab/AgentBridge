using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIOrchestrator;
using Microsoft.Extensions.Configuration;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Sys;
using SIPSorceryMedia.Abstractions;
// Localized strings: the generated resx class is aliased (it clashes with the generic
// System.Collections.Generic.Dictionary). NEVER inline user-facing text — see the banner below.
using Lang = AgentBridge.Resources.Dictionary;

// ═══════════════════════════════════════════════════════════════════════
//  LOCALIZATION INVARIANT — project rule, ALWAYS visible (read before editing):
//  NEVER put user-facing text in this file. Every string belongs in the language
//  dictionaries (Resources/Dictionary.resx + Dictionary.{it,de,es,fr,ru}.resx)
//  and MUST be read through Dictionary.<Key>, translated in ALL supported
//  languages. The culture is set from the resolved call language — never a
//  hardcoded pair. (The old `Italian ? "…" : "…"` announcements violated this
//  and supported only 2 of the 5 languages.)
// ═══════════════════════════════════════════════════════════════════════
//
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
        /// <summary>REGISTER expiry/refresh interval in seconds (default 60). Home NAT mappings
        /// often time out well before the SIP default 300 s: with a too-long interval, inbound
        /// calls between refreshes go unanswered — the entry point still holds the registration,
        /// but the router has dropped the mapping and the INVITE never arrives. 60 s keeps the
        /// pinhole alive on consumer routers.</summary>
        public int RegisterExpiry { get; set; } = 60;
        /// <summary>Seconds to wait for the PIN before the server hangs up the call (default 60,
        /// min 10). A call that reaches the PIN gate and receives nothing ends instead of
        /// staying open forever.</summary>
        public int PinTimeoutSeconds { get; set; } = 60;
        /// <summary>Seconds after the caller's speech ends before the processing indicator (the
        /// looped "data processing" cue) starts playing (default 2). The cue is armed by the
        /// subprocess VAD "end" event — i.e. the moment STT/LLM processing begins — so a caller
        /// never stares at silence while the agent computes. Set higher to delay the cue (it can
        /// sound noisy on a quiet line), lower to reassure sooner.</summary>
        public int IndicatorDelaySeconds { get; set; } = 2;
        /// <summary>Whisper model used by the STT subprocess (tiny/base/small/medium/largev2/
        /// largev3).
        /// ⚠️ DO NOT GO BELOW "small": tiny/base were tested on real phone calls and their
        /// recognition is unusable (e.g. "il meteo" → "villioni sul metto"). "small" is the
        /// minimum that works acceptably on real G.711 audio (≈7 s per utterance on CPU —
        /// the accuracy trade-off is worth it). Larger models (medium+) are more accurate but
        /// far too slow on this CPU.</summary>
        public string SttModel { get; set; } = "small";
        /// <summary>Whisper quantization for the STT subprocess (empty/q4_0/q4_1/q5_0/q5_1/q8_0).
        /// Default <c>q8_0</c>: "small-q8_0" is ~2-3x FASTER on CPU (measured STT 8.3 s → ~3-4 s)
        /// with minimal accuracy loss — the latency fix for phone calls, where whisper is the
        /// recognizer by necessity (RTP audio, not the mic → WinRT is not usable). Set empty to
        /// keep the full FP16 model.</summary>
        public string SttQuant { get; set; } = "q8_0";
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
        public volatile bool MediaAttached;     // set once the RTP capture is live (see the watchdog)
        public DateTime PinDeadline;            // moment the PIN gate gives up (EnforcePinTimeoutAsync)
    }

    /// <summary>
    /// The SIP media endpoint: implements <see cref="IAudioMedia"/> so the WHOLE conversation is
    /// driven by <see cref="VoiceConversation.RunConversationAsync"/> — the medium is a mere I/O
    /// channel: RTP audio in (G.711 → VAD → whisper → <see cref="SpeechReceived"/>) and Kokoro TTS
    /// out (raw PCM → RTP). No conversation logic lives here (see AIOrchestrator/ARCHITECTURE.md).
    /// </summary>
    private sealed class SipVoiceMedia : IAudioMedia
    {
        /// <summary>One TTS output unit: a real reply chunk or a processing-indicator piece
        /// (tagged so the pump can discard queued indicator pieces the moment the reply starts).</summary>
        private sealed record TtsChunk(byte[] Pcm, bool Indicator);

        private readonly CallContext _call;
        private readonly DtmfDetector _dtmf = new();
        private readonly System.Threading.Channels.Channel<byte[]> _inputQueue = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        private readonly System.Threading.Channels.Channel<TtsChunk> _ttsQueue = System.Threading.Channels.Channel.CreateUnbounded<TtsChunk>();
        private int _ttsPending;
        private Task _inputPump = Task.CompletedTask;
        private Task _ttsPump = Task.CompletedTask;
        private volatile bool _speaking;
        private volatile bool _conversationActive;   // set by StartAsync: distinguishes conversation replies from pre-PIN announcements
        private bool _pinAudioLogged;
        private int _firstChunkLogged;               // diagnostic: when the first TTS chunk hits RTP

        // Processing indicator: a looped "data processing" cue played to the caller while the
        // agent computes (STT/LLM/tools) — armed by the subprocess VAD "end" event (speech ended,
        // processing began), starts IndicatorDelaySeconds later and stops the moment the first
        // reply chunk arrives. Fills the latency void so the caller knows the line is working.
        // Sent over the MEDIA (RTP) — never played locally. While the caller speaks again
        // (VAD "speech"), the cue is paused so it never beeps over the caller's own voice.
        private byte[]? _indicatorPcm = LoadIndicatorPcm();
        private DateTime _processingSince;
        private volatile bool _replyStarted;
        private volatile bool _indicatorPaused;
        private Task _indicatorLoop = Task.CompletedTask;
        private const int IndicatorPieceMs = 400;    // pieces this small bound the reply delay to ~400 ms
        private const int IndicatorMaxSeconds = 25;  // hard cap — a stalled STT/LLM must never beep forever
        private const int PostTtsMuteMs = 500;       // capture stays muted this long after the last TTS
                                                     // chunk: the caller's echo of the reply is delayed
                                                     // by network + phone latency, not real-time

        public SipVoiceMedia(CallContext call)
        {
            _call = call;
            // In-band keypad tones (for clients that cannot emit RFC 4733 events or SIP INFO):
            // fed to the same PIN gate as the RTP-event digits. Active ONLY while the call is
            // in the PIN phase — once the conversation starts, speech must never be misread
            // as tones (the detector is disabled).
            _dtmf.DigitDetected += d =>
            {
                Log.LogStep($"SIP in-band DTMF: {d}");
                HandleDtmfDigit(d, 0);
            };
            // STT lives in the persistent subprocess (VAD + whisper); transcripts arrive here.
            SipVoiceAgent.Transcript += OnSubprocessTranscript;
            // The subprocess VAD reports "speech"/"end" — the indicator is armed at "end"
            // (processing start) and paused at "speech" (the caller is talking, not waiting).
            SipVoiceAgent.VadState += OnVadState;
        }

        public string Language => VoiceConversation.ResolveLang(Cfg.Lang);

        public event Action<string>? SpeechReceived;

        public Task StartAsync(CancellationToken ct = default)
        {
            // RTP capture is attached at call setup (Attach) so the in-band DTMF detector also
            // hears the PIN phase; here only the conversation-specific wiring is added.
            _conversationActive = true;
            Log.LogStep("SIP RTP capture attached (conversation media loop started)");
            return Task.CompletedTask;
        }

        /// <summary>Attaches the RTP capture for the WHOLE call (PIN phase included): the
        /// in-band DTMF detector needs the caller's audio before the conversation starts, and the
        /// TTS pump must be live from the first announcement (the spoken welcome precedes the
        /// conversation, which is when VoiceConversation calls StartAsync).</summary>
        public void Attach()
        {
            _call.Media.OnRtpPacketReceived += OnRtpAudio;
            _dtmf.Reset();
            StartPumps();
        }

        public Task StopAsync()
        {
            _call.Media.OnRtpPacketReceived -= OnRtpAudio;
            SipVoiceAgent.Transcript -= OnSubprocessTranscript;
            SipVoiceAgent.VadState -= OnVadState;
            _inputQueue.Writer.TryComplete();
            _ttsQueue.Writer.TryComplete();
            _dtmf.Reset();
            _conversationActive = false;
            return Task.CompletedTask;
        }

        // Decodes G.711 payloads (1 byte = 1 sample @ 8 kHz) into 16-bit PCM. RFC 4733 DTMF
        // events (dynamic payload type) are skipped — they never reach the STT. Capture is paused
        // while the TTS reply plays (no barge-in: a hands-free caller would otherwise echo the
        // reply back into the subprocess STT). In conversation the PCM is forwarded to the
        // persistent voice subprocess (VAD + whisper live there — media is I/O only).
        private void OnRtpAudio(IPEndPoint _, SDPMediaTypesEnum __, RTPPacket packet)
        {
            if (_speaking) return;
            var phase = _call.Phase;
            if (phase != CallPhase.Pin && phase != CallPhase.Conversation) return;
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
            if (phase == CallPhase.Pin)
            {
                if (!_pinAudioLogged)
                {
                    _pinAudioLogged = true;
                    Log.LogStep($"SIP pin-phase audio capture active ({pcm.Length} bytes first packet)");
                }
                _dtmf.Feed(pcm);       // in-band keypad tones only (RFC 4733 handled by OnDtmfTone)
            }
            else
            {
                _inputQueue.Writer.TryWrite(Upsample8kTo16k(pcm));   // conversation speech → subprocess STT
            }
        }

        /// <summary>Linear 8→16 kHz upsample of PCM16 (whisper is trained on 16 kHz).</summary>
        private static byte[] Upsample8kTo16k(byte[] pcm8k)
        {
            var samples = pcm8k.Length / 2;
            var outPcm = new byte[samples * 4];
            for (int i = 0; i < samples; i++)
            {
                var s = (short)(pcm8k[i * 2] | pcm8k[i * 2 + 1] << 8);
                var next = i + 1 < samples ? (short)(pcm8k[(i + 1) * 2] | pcm8k[(i + 1) * 2 + 1] << 8) : s;
                var mid = (short)((s + next) / 2);
                var o = i * 4;
                outPcm[o] = (byte)(s & 0xFF); outPcm[o + 1] = (byte)((s >> 8) & 0xFF);
                outPcm[o + 2] = (byte)(mid & 0xFF); outPcm[o + 3] = (byte)((mid >> 8) & 0xFF);
            }
            return outPcm;
        }

        /// <summary>Transcript from the persistent subprocess (VAD done there).</summary>
        private void OnSubprocessTranscript(string text)
        {
            if (!_conversationActive) return;
            // Whisper labels non-speech segments as "[Musica]"/"[Rumore]"/"[Applausi]" etc. When
            // the whole utterance is such a placeholder (background music, line noise), it must
            // never reach the LLM — the agent would "answer the music". Mixed text is kept.
            if (IsNoiseOnlyTranscript(text))
            {
                // Nothing to process → cancel the cue armed at VAD "end" (it must not beep for
                // background music the agent will never answer).
                _indicatorPaused = true;
                return;
            }
            Log.LogStep($"SIP caller said: {text}");
            // The caller's utterance is now being processed (STT done → LLM/tools may take a
            // while): arm the processing indicator — it starts after IndicatorDelaySeconds and
            // loops until the first reply chunk arrives. Re-arming here is a safety net: the
            // primary arming happened at the VAD "end" event (before whisper ran).
            _processingSince = DateTime.UtcNow;
            _replyStarted = false;
            _indicatorPaused = false;
            EnsureIndicatorLoop();
            SpeechReceived?.Invoke(text);
        }

        /// <summary>VAD transitions from the subprocess: "speech" = the caller started talking
        /// (pause the cue — they are speaking, not waiting), "end" = the utterance closed and
        /// transcription began (arm the cue — processing has started). "end" always follows a
        /// "speech", so a cue never starts while the caller is still talking.</summary>
        private void OnVadState(string state)
        {
            if (!_conversationActive) return;
            switch (state)
            {
                case "speech":
                    _indicatorPaused = true;      // never beep over the caller's own voice
                    break;
                case "end":
                    _processingSince = DateTime.UtcNow;
                    _replyStarted = false;
                    _indicatorPaused = false;
                    EnsureIndicatorLoop();
                    break;
            }
        }

        /// <summary>Drains queued conversation PCM into the persistent subprocess, in order.</summary>
        private void StartPumps()
        {
            if (!_inputPump.IsCompleted) return;
            _inputPump = Task.Run(async () =>
            {
                await foreach (var pcm in _inputQueue.Reader.ReadAllAsync())
                    await SipVoiceAgent.SendAudioAsync(pcm, _call.Cts.Token);
            });
            if (!_ttsPump.IsCompleted) return;
            _ttsPump = Task.Run(async () =>
            {
                await foreach (var chunk in _ttsQueue.Reader.ReadAllAsync())
                {
                    // Indicator pieces are discarded the moment the real reply starts (bounded to
                    // ~1 in-flight piece = IndicatorPieceMs, so the reply is not delayed by the
                    // cue) and while the cue is paused (the caller is talking again).
                    if (chunk.Indicator && (_replyStarted || _indicatorPaused)) { Interlocked.Decrement(ref _ttsPending); continue; }
                    try
                    {
                        await _call.Media.AudioExtrasSource.SendAudioFromStream(new MemoryStream(chunk.Pcm), AudioSamplingRatesEnum.Rate24kHz);
                    }
                    catch { }
                    Interlocked.Decrement(ref _ttsPending);
                }
            });
        }

        /// <summary>Loops the processing cue over RTP: starts <see cref="SipConfig.IndicatorDelaySeconds"/>
        /// after the utterance was acquired and keeps pushing 400 ms pieces until the first reply
        /// chunk arrives (then the pump discards whatever is left). Pauses while the caller speaks
        /// again; hard-stops after <see cref="IndicatorMaxSeconds"/> so a stalled STT/LLM can never
        /// beep forever.</summary>
        private void EnsureIndicatorLoop()
        {
            if (_indicatorPcm == null) return;
            if (!_indicatorLoop.IsCompleted) return;
            _indicatorLoop = Task.Run(async () =>
            {
                var pieceBytes = IndicatorPieceMs * 48;   // 24 kHz × 2 B = 48 B/ms
                DateTime? sentSince = null;
                try
                {
                    while (_conversationActive && !_replyStarted && !_call.Cts.IsCancellationRequested)
                    {
                        if (!_indicatorPaused &&
                            (DateTime.UtcNow - _processingSince).TotalSeconds >= Math.Max(1, Cfg.IndicatorDelaySeconds))
                        {
                            sentSince ??= DateTime.UtcNow;
                            if ((DateTime.UtcNow - sentSince.Value).TotalSeconds >= IndicatorMaxSeconds) return;
                            for (int off = 0; off < _indicatorPcm.Length; off += pieceBytes)
                            {
                                if (_indicatorPaused || _replyStarted || _call.Cts.IsCancellationRequested) break;
                                var piece = _indicatorPcm.AsSpan(off, Math.Min(pieceBytes, _indicatorPcm.Length - off)).ToArray();
                                _ttsQueue.Writer.TryWrite(new TtsChunk(piece, true));
                                Interlocked.Increment(ref _ttsPending);
                                // Play the piece in real time (48 B per ms) before the next one.
                                await Task.Delay(piece.Length / 48, _call.Cts.Token);
                                if (_replyStarted || _call.Cts.IsCancellationRequested) return;
                            }
                        }
                        await Task.Delay(150, _call.Cts.Token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log.LogStep($"SIP processing indicator error: {ex.Message}"); }
            });
        }

        /// <summary>True when the transcript contains ONLY non-speech placeholders (whisper's
        /// "[Musica]", "[Rumore]", "[Music]", "[Noise]", ...) — i.e. the utterance carried no
        /// real speech and must not be fed to the LLM.</summary>
        private static bool IsNoiseOnlyTranscript(string text)
        {
            foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (!(part.Length > 2 && part[0] == '[' && part[^1] == ']'))
                    return false;
            return true;
        }

        /// <summary>Loads the processing-indicator cue (assets/processing-indicator.wav — 24 kHz
        /// mono PCM16, deployed with the server) as raw PCM for looping over RTP. Null when the
        /// asset is missing (the indicator is simply skipped).</summary>
        private static byte[]? LoadIndicatorPcm()
        {
            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "processing-indicator.wav");
                if (!File.Exists(path)) { Log.LogStep("SIP processing indicator: asset not found (assets/processing-indicator.wav)"); return null; }
                var bytes = File.ReadAllBytes(path);
                var off = 12;
                while (off + 8 <= bytes.Length)
                {
                    var id = System.Text.Encoding.ASCII.GetString(bytes, off, 4);
                    var size = BitConverter.ToInt32(bytes, off + 4);
                    if (id == "data") return NormalizePeak(bytes.AsSpan(off + 8, Math.Min(size, bytes.Length - off - 8)).ToArray());
                    off += 8 + size + (size % 2);
                }
                Log.LogStep("SIP processing indicator: no data chunk in the asset WAV");
                return null;
            }
            catch (Exception ex)
            {
                Log.LogStep($"SIP processing indicator load failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Amplifies the cue to near-full-scale (peak ≈ 0.9 × int16 max) so the caller
        /// hears it clearly — RTP has no volume knob, the only "playback volume" is the PCM
        /// amplitude we send. The gain is capped (a silent asset must not be boosted into noise)
        /// and samples are clamped to avoid clipping distortion.</summary>
        private static byte[] NormalizePeak(byte[] pcm)
        {
            int peak = 1;
            for (int i = 0; i + 1 < pcm.Length; i += 2)
            {
                var s = Math.Abs((short)(pcm[i] | pcm[i + 1] << 8));
                if (s > peak) peak = s;
            }
            var gain = Math.Min(0.9 * short.MaxValue / peak, 8.0);
            if (gain <= 1.01) return pcm;
            for (int i = 0; i + 1 < pcm.Length; i += 2)
            {
                var s = (int)Math.Round((short)(pcm[i] | pcm[i + 1] << 8) * gain);
                s = Math.Clamp(s, short.MinValue, short.MaxValue);
                pcm[i] = (byte)(s & 0xFF);
                pcm[i + 1] = (byte)((s >> 8) & 0xFF);
            }
            Log.LogStep($"SIP processing indicator: amplified ×{gain:F2} (peak {peak} → {0.9 * short.MaxValue:F0})");
            return pcm;
        }

        /// <summary>Renders speakable text to the caller: the persistent voice subprocess renders
        /// Kokoro/SAPI PCM (streamed sentence by sentence) → raw PCM → RTP. Media is I/O only —
        /// no TTS engine lives here (see ARCHITECTURE.md).</summary>
        public async Task SpeakAsync(string text, bool isLast, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (_call.Media.IsClosed) return;
            if (!SipVoiceAgent.IsReady) return;
            if (_conversationActive) Log.LogStep($"SIP agent replied: {text}");
            _speaking = true;
            _firstChunkLogged = 0;   // measure time-to-first-audio of THIS reply
            _replyStarted = true;    // stop the processing indicator — the real reply is here
            try
            {
                foreach (var sentence in SplitSentences(text))
                {
                    if (ct.IsCancellationRequested) return;
                    await SipVoiceAgent.SpeakAsync(sentence, Language, pcm =>
                    {
                        if (_firstChunkLogged == 0) { _firstChunkLogged = 1; Log.LogStep($"SIP TTS first-chunk t={DateTime.UtcNow:HH:mm:ss.fff}"); }
                        _ttsQueue.Writer.TryWrite(new TtsChunk(pcm, false));
                        Interlocked.Increment(ref _ttsPending);
                    }, ct);
                }
                // Sentences are enqueued back-to-back (the single ordered queue preserves the
                // order); only the FINAL drain waits, so capture stays paused until all RTP is sent.
                while (Volatile.Read(ref _ttsPending) > 0 && !ct.IsCancellationRequested)
                    await Task.Delay(15, ct);
            }
            finally
            {
                // Post-TTS echo guard: the caller's phone plays the reply through its speaker, and
                // the echo comes BACK delayed by network + phone latency. Keep capture muted
                // PostTtsMuteMs after the last chunk so the subprocess VAD never hears our own
                // answer (recognition itself is continuous — mute, don't stop/start).
                if (!ct.IsCancellationRequested) await Task.Delay(PostTtsMuteMs, CancellationToken.None);
                _speaking = false;
            }
        }

        /// <summary>Splits a reply into sentences (on ".!?"), keeping empty chunks out. Short
        /// replies stay as one chunk; long ones stream one sentence at a time.</summary>
        private static IEnumerable<string> SplitSentences(string text)
        {
            var sentences = text.Split(['.', '!', '?'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (sentences.Length <= 1) return new[] { text };
            return sentences;
        }
    }

    /// <summary>Goertzel-based in-band DTMF detector on 8 kHz PCM16. Emits each digit once after
    /// ~50 ms of a steady tone pair and requires a ~75 ms gap before accepting the next digit
    /// (a held key never repeats). Used ONLY during the PIN phase — SipVoiceMedia stops feeding
    /// it once the conversation starts, so speech is never misread as keypad tones.</summary>
    private sealed class DtmfDetector
    {
        private const int FrameSamples = 205;          // ~25.6 ms @ 8 kHz (≥ one DTMF tone period)
        private const int MinValidFrames = 2;          // ~51 ms steady tone before emitting
        private const int GapFrames = 3;               // ~77 ms silence/detune before re-arming
        private const double AbsThreshold = 0.015;     // normalized power floor for row+col
        private const double RatioThreshold = 3.0;     // chosen freq must beat its group's second

        private static readonly double[] RowFreqs = { 697, 770, 852, 941 };
        private static readonly double[] ColFreqs = { 1209, 1336, 1477, 1633 };
        private static readonly string[] Matrix = { "123A", "456B", "789C", "*0#D" };

        private readonly double[] _coeffs = new double[8];
        private readonly double[] _q1 = new double[8];
        private readonly double[] _q2 = new double[8];
        private readonly short[] _frame = new short[FrameSamples];
        private int _frameLen;
        private int _validCount;
        private int _gapCount;
        private byte? _lastDigit;

        public event Action<byte>? DigitDetected;

        public DtmfDetector()
        {
            for (int i = 0; i < 8; i++)
            {
                var f = i < 4 ? RowFreqs[i] : ColFreqs[i - 4];
                var k = (int)Math.Round(0.5 + FrameSamples * f / 8000.0);
                _coeffs[i] = 2.0 * Math.Cos(2.0 * Math.PI * k / FrameSamples);
            }
        }

        public void Reset()
        {
            _frameLen = 0;
            _validCount = 0;
            _gapCount = 0;
            _lastDigit = null;
            Array.Clear(_q1);
            Array.Clear(_q2);
        }

        public void Feed(ReadOnlySpan<byte> pcm)
        {
            for (int i = 0; i + 1 < pcm.Length; i += 2)
            {
                _frame[_frameLen++] = (short)(pcm[i] | pcm[i + 1] << 8);
                if (_frameLen == FrameSamples)
                {
                    DetectFrame();
                    _frameLen = 0;
                }
            }
        }

        private void DetectFrame()
        {
            // Goertzel recurrence over the frame, once per candidate frequency.
            Array.Clear(_q1);
            Array.Clear(_q2);
            for (int n = 0; n < FrameSamples; n++)
            {
                var x = _frame[n] / 32768.0;
                for (int i = 0; i < 8; i++)
                {
                    var s = x + _coeffs[i] * _q1[i] - _q2[i];
                    _q2[i] = _q1[i];
                    _q1[i] = s;
                }
            }
            var norm = (FrameSamples / 2.0) * (FrameSamples / 2.0);
            double[] power = new double[8];
            for (int i = 0; i < 8; i++)
                power[i] = (_q2[i] * _q2[i] + _q1[i] * _q1[i] - _coeffs[i] * _q1[i] * _q2[i]) / norm;

            var row = Best(power, 0, 4);
            var col = Best(power, 4, 8);
            var valid = row.Power >= AbsThreshold && col.Power >= AbsThreshold &&
                        row.Power >= RatioThreshold * row.Second && col.Power >= RatioThreshold * col.Second;

            if (valid)
            {
                var digit = (byte)Matrix[row.Index][col.Index];
                if (_lastDigit != digit)
                {
                    _lastDigit = digit;
                    _validCount = 1;
                    _gapCount = 0;
                }
                else if (++_validCount == MinValidFrames)
                {
                    DigitDetected?.Invoke(digit);
                }
            }
            else
            {
                _gapCount++;
                if (_gapCount >= GapFrames)
                {
                    _validCount = 0;
                    _gapCount = 0;
                    _lastDigit = null;
                }
            }
        }

        private static (int Index, double Power, double Second) Best(double[] power, int from, int to)
        {
            // i1 = largest, i2 = second largest (must be DISTINCT indices: the group-ratio
            // check compares the winner against the runner-up, never against itself).
            int i1 = from, i2 = from + 1;
            if (power[i2] > power[i1]) (i1, i2) = (i2, i1);
            for (int i = from + 2; i < to; i++)
            {
                if (power[i] > power[i1]) { i2 = i1; i1 = i; }
                else if (power[i] > power[i2]) i2 = i;
            }
            return (i1, power[i1], power[i2]);
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
                stt_available = SipVoiceAgent.IsReady,
                tts_available = SipVoiceAgent.IsReady,
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
            // The persistent voice subprocess (VAD + whisper STT + Kokoro/SAPI TTS) is started
            // once for the whole server lifetime: the whisper model stays loaded → persistent
            // STT. Both the announcements and the conversation go through it (media = I/O only).
            SipVoiceAgent.ExePath = ResolveSttExe();
            SipVoiceAgent.SttModel = Cfg.SttModel;
            SipVoiceAgent.SttQuant = Cfg.SttQuant;
            await SipVoiceAgent.StartAsync(Cfg.Lang);

            var transport = new SIPTransport();
            transport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, Cfg.ListenPort)));
            var ua = CreateUserAgent(transport);

            SIPRegistrationUserAgent? registration = null;
            if (!string.IsNullOrWhiteSpace(Cfg.Registrar) && !string.IsNullOrWhiteSpace(Cfg.Username))
            {
                registration = new SIPRegistrationUserAgent(transport, Cfg.Username, Cfg.Password,
                    Cfg.Registrar, Math.Max(30, Cfg.RegisterExpiry), exitOnUnequivocalFailure: false);
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
            if (!SipVoiceAgent.IsReady)
                Log.LogStep("SIP warning: voice subprocess unavailable (AIOffice.VoiceAgent missing in voiceagent-stt/) — announcements and agent replies will be silent for callers");
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
        SipVoiceAgent.Stop();
    }

    // ─── Config management (TUI ↔ appsettings.json "Sip" section) ─────────

    /// <summary>Keys whose change requires a SIP transport restart (bind/REGISTER are
    /// transport-level). Every other key is read per call and applies to the next one.</summary>
    private static readonly HashSet<string> RestartKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(SipConfig.Enabled), nameof(SipConfig.ListenPort), nameof(SipConfig.Registrar),
        nameof(SipConfig.Username), nameof(SipConfig.Password), nameof(SipConfig.RtpPortRange),
        nameof(SipConfig.RegisterExpiry),
    };

    /// <summary>Keys cached by the PIN gate (rebuilt when they change).</summary>
    private static readonly HashSet<string> GateKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(SipConfig.Pin), nameof(SipConfig.MaxPinAttempts), nameof(SipConfig.LockoutHours),
    };

    /// <summary>Read-only snapshot of the effective SIP configuration, secrets masked
    /// (Pin/Password report only whether they are set). Consumed by GET /v1/sip/config.</summary>
    public static object ConfigSnapshot
    {
        get
        {
            var c = Cfg;
            return new
            {
                enabled = c.Enabled,
                listen_port = c.ListenPort,
                registrar = c.Registrar,
                username = c.Username,
                password_set = !string.IsNullOrEmpty(c.Password),
                answer_mode = c.AnswerMode,
                pin_set = !string.IsNullOrEmpty(c.Pin),
                max_pin_attempts = c.MaxPinAttempts,
                lockout_hours = c.LockoutHours,
                register_expiry = c.RegisterExpiry,
                pin_timeout_seconds = c.PinTimeoutSeconds,
                indicator_delay_seconds = c.IndicatorDelaySeconds,
                allowed_callers = c.AllowedCallers,
                agent = c.Agent,
                lang = c.Lang,
                stt_exe_path = c.SttExePath,
                stt_model = c.SttModel,
                stt_quant = c.SttQuant,
                rtp_port_range = c.RtpPortRange,
            };
        }
    }

    /// <summary>Sets one SIP config key, persists the whole "Sip" section back to
    /// appsettings.json and applies the change. Returns an error message, whether a transport
    /// restart was needed/applied, and a human-readable outcome.</summary>
    public static async Task<(string? Error, bool RestartRequired, string Message)> SetConfigAsync(string key, string? value)
    {
        var prop = typeof(SipConfig).GetProperty(key, System.Reflection.BindingFlags.IgnoreCase |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (prop == null) return ($"unknown SIP config key: {key}", false, "");

        object? parsed;
        try { parsed = ParseConfigValue(prop.PropertyType, value); }
        catch (Exception ex) { return ($"invalid value for {key}: {ex.Message}", false, ""); }

        var restarting = RestartKeys.Contains(key);
        CallContext? call;
        lock (Sync) call = Call;
        if (restarting && call != null)
            return ($"{key} needs a SIP transport restart — hang up the active call first", false, "");

        var previous = prop.GetValue(Cfg);
        prop.SetValue(Cfg, parsed);
        var persistError = PersistConfig();
        if (persistError != null)
        {
            prop.SetValue(Cfg, previous);   // roll back the in-memory change
            return (persistError, false, "");
        }
        if (GateKeys.Contains(key)) ApplyRuntime();

        if (!restarting) return (null, false, $"{key} set to {DisplayValue(key, parsed)} — active from the next call");
        var restartError = await RestartTransportAsync();
        var state = Cfg.Enabled ? "transport restarted" : "transport stopped";
        return (restartError, true, restartError ?? $"{key} set to {DisplayValue(key, parsed)} — {state}");
    }

    /// <summary>Re-reads the "Sip" section from appsettings.json (hand edits made outside the
    /// TUI) and applies it live. Returns an error, whether a transport restart was needed, and
    /// a human-readable outcome.</summary>
    public static async Task<(string? Error, bool RestartRequired, string Message)> ReloadConfigAsync()
    {
        try
        {
            var path = ConfigFilePath();
            if (!File.Exists(path)) return ($"appsettings.json not found at {path}", false, "");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("Sip", out var section))
                return ("no Sip section in appsettings.json", false, "");
            var fileCfg = section.Deserialize<SipConfig>() ?? new SipConfig();
            fileCfg.Pin = (fileCfg.Pin ?? "").Trim();
            fileCfg.MaxPinAttempts = Math.Max(1, fileCfg.MaxPinAttempts);
            fileCfg.LockoutHours = Math.Max(1, fileCfg.LockoutHours);

            var restarting = RestartKeys.Any(k => ConfigValueDiffers(Cfg, fileCfg, k));
            CallContext? call;
            lock (Sync) call = Call;
            if (restarting && call != null)
                return ("the file changed a transport-level key — hang up the active call and reload again", false, "");

            if (GateKeys.Any(k => ConfigValueDiffers(Cfg, fileCfg, k)))
            {
                var prevLock = Gate.LockedUntilUtc;
                Cfg = fileCfg;
                Gate = new PinAuthGate(Cfg.Pin, Cfg.MaxPinAttempts, TimeSpan.FromHours(Cfg.LockoutHours));
                Gate.RestoreLockout(prevLock);
            }
            else
            {
                Cfg = fileCfg;
            }

            if (!restarting) return (null, false, "SIP config reloaded from appsettings.json");
            var restartError = await RestartTransportAsync();
            var state = Cfg.Enabled ? "transport restarted" : "transport stopped";
            return (restartError, true, restartError ?? $"SIP config reloaded — {state}");
        }
        catch (Exception ex)
        {
            return ($"SIP config reload failed: {ex.Message}", false, "");
        }
    }

    private static string ConfigFilePath() =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

    private static object? ParseConfigValue(Type type, string? value)
    {
        if (type == typeof(bool)) return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                                        value == "1" || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
        if (type == typeof(int)) return int.Parse((value ?? "").Trim(), System.Globalization.CultureInfo.InvariantCulture);
        if (type == typeof(List<string>))
            return (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return value ?? "";
    }

    private static bool ConfigValueDiffers(SipConfig a, SipConfig b, string key) =>
        !Equals(typeof(SipConfig).GetProperty(key)!.GetValue(a), typeof(SipConfig).GetProperty(key)!.GetValue(b));

    private static string DisplayValue(string key, object? value)
    {
        if (key.Equals(nameof(SipConfig.Pin), StringComparison.OrdinalIgnoreCase)) return "•••• (PIN)";
        if (key.Equals(nameof(SipConfig.Password), StringComparison.OrdinalIgnoreCase)) return "•••• (password)";
        return value?.ToString() ?? "";
    }

    /// <summary>Rebuilds the PIN gate after a PIN-policy change; the persisted lockout state
    /// carries over (the wrong-attempt counter resets with a new PIN).</summary>
    private static void ApplyRuntime()
    {
        var prevLock = Gate.LockedUntilUtc;
        Gate = new PinAuthGate(Cfg.Pin, Cfg.MaxPinAttempts, TimeSpan.FromHours(Cfg.LockoutHours));
        Gate.RestoreLockout(prevLock);
    }

    private static async Task<string?> RestartTransportAsync()
    {
        Stop();
        if (!Cfg.Enabled) return null;
        return await StartAsync();
    }

    /// <summary>Persists the effective "Sip" section back to appsettings.json, preserving every
    /// other section of the file. appsettings.json is a runtime file for this server (updates
    /// never overwrite it — see AutoUpdate.cs), so it is safe to write from here.</summary>
    private static string? PersistConfig()
    {
        try
        {
            var path = ConfigFilePath();
            if (!File.Exists(path)) return $"appsettings.json not found at {path}";
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
            root["Sip"] = JsonSerializer.SerializeToNode(Cfg);
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Log.LogStep("SIP config persisted to appsettings.json");
            return null;
        }
        catch (Exception ex)
        {
            return $"SIP config persist failed: {ex.Message}";
        }
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
            // A fresh INVITE is being set up right now (AcceptCall ran, but Call is not yet
            // registered — a sub-millisecond window in OnIncomingCall): rebuilding the user
            // agent mid-setup would kill the call. The gate is held for the whole setup.
            if (CallGate.CurrentCount == 0) return;
            Log.LogStep("SIP user agent cleanup failed — rebuilding it (stale dialog would drop new INVITEs)");
            try { ua.Close(); } catch { }
            Ua = CreateUserAgent(transport);
            return;
        }
        // We still hold a call the user agent reports as inactive: the remote hangup reached
        // the transport but OnCallHungup was missed. The stale internal dialog would silently
        // drop every later INVITE — clear the orphan and rebuild now, instead of waiting for
        // the next INVITE to trip the transport-level handler. ONLY once the call is fully
        // established (MediaAttached): between "Call registered" and "Answer/Attach complete"
        // the dialog does not exist yet and a fresh call must never be misread as an orphan —
        // clearing Call mid-setup makes HandleDtmfDigit drop every PIN digit and the call
        // goes dead (reproduced by SipSmoke --voice-only's immediate call, which collides
        // with the 5 s watchdog tick).
        if (!ua.IsCallActive && call.MediaAttached)
        {
            Log.LogStep("SIP orphaned call state cleared by watchdog (user agent has no active dialog)");
            lock (Sync) if (Call == call) Call = null;
            try { ua.Close(); } catch { }
            Ua = CreateUserAgent(transport);
        }
    }

    private static async Task OnTransportRequest(SIPEndPoint _, SIPEndPoint __, SIPRequest request)
    {
        if (request.Method == SIPMethodsEnum.INFO)
        {
            await HandleInfoDtmfAsync(request);
            return;
        }
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

    /// <summary>DTMF over SIP INFO (RFC 2976): clients that cannot emit RFC 4733 RTP events
    /// send the keypad digit as an INFO body. The shared user agent does not handle INFO at
    /// all (non-exclusive transport → it stays silent), so this transport-level hook answers
    /// 200 and feeds the digit to the same PIN gate. Only in-dialog INFO of the ACTIVE call
    /// is consumed; anything else is left to the user agent.</summary>
    private static async Task HandleInfoDtmfAsync(SIPRequest request)
    {
        CallContext? call;
        lock (Sync) call = Call;
        if (call == null || call.CallId == null || request.Header.CallId != call.CallId ||
            request.Header.To?.ToTag == null) return;

        try
        {
            await Transport!.SendResponseAsync(SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ok, null));
        }
        catch { }

        var tone = ParseInfoDtmf(request);
        if (tone is { } t)
        {
            Log.LogStep($"SIP INFO DTMF: {t}");
            HandleDtmfDigit(t, 0);
        }
    }

    private static byte? ParseInfoDtmf(SIPRequest request)
    {
        var body = request.Body ?? "";
        // application/dtmf-relay (Cisco/Asterisk): "Signal = 7" (also "Signal: 7", lowercase).
        var m = Regex.Match(body, @"(?im)^\s*Signal\s*[=:]\s*([0-9#*a-dA-D])\s*$");
        if (m.Success) return MapDtmfChar(m.Groups[1].Value[0]);
        // Bare-digit bodies ("7", "7\r\n") used by some RFC 4733-style INFO senders.
        m = Regex.Match(body, @"(?m)^\s*([0-9#*a-dA-D])\s*$");
        return m.Success ? MapDtmfChar(m.Groups[1].Value[0]) : null;
    }

    private static byte? MapDtmfChar(char c)
    {
        c = char.ToUpperInvariant(c);
        if (c is >= '0' and <= '9') return (byte)(c - '0');
        return c switch { '*' => 10, '#' => 11, _ => null };
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
            call.MediaAttached = true;   // the outbound dialog is up — the watchdog may clear orphans now
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
                call.VoiceMedia.Attach();   // RTP capture for the whole call (PIN phase included)
                call.MediaAttached = true;  // the dialog is up — the watchdog may clear orphans now
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
                StartPinTimeout(call);   // hang up if no PIN arrives within PinTimeoutSeconds
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
    // One shared entry point for every digit source: RFC 4733 RTP events (OnDtmfTone),
    // SIP INFO bodies (HandleInfoDtmfAsync) and in-band keypad tones (DtmfDetector).

    private static void OnDtmfTone(byte tone, int durationMs) => HandleDtmfDigit(tone, durationMs);

    private static void HandleDtmfDigit(byte tone, int durationMs)
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

    // ─── PIN timeout ──────────────────────────────────────────────────
    // A call left in the PIN phase with no digits ends itself after PinTimeoutSeconds:
    // the caller hears a short notice, then the server hangs up (same pattern as the
    // lockout path). The watcher exits as soon as the phase changes or the call ends.

    private static void StartPinTimeout(CallContext call)
    {
        call.PinDeadline = DateTime.UtcNow.AddSeconds(Math.Max(10, Cfg.PinTimeoutSeconds));
        _ = Task.Run(() => EnforcePinTimeoutAsync(call));
    }

    private static async Task EnforcePinTimeoutAsync(CallContext call)
    {
        try
        {
            while (call.Phase == CallPhase.Pin && DateTime.UtcNow < call.PinDeadline)
                await Task.Delay(1000, call.Cts.Token);
            if (call.Phase != CallPhase.Pin) return;   // PIN accepted or the call ended

            Log.LogStep("SIP PIN timeout — ending the call");
            await call.VoiceMedia!.SpeakAsync(AnnouncePinTimeout(), true, call.Cts.Token);
            try { Ua?.Hangup(); } catch (Exception ex) { Log.LogStep($"SIP pin-timeout hangup failed: {ex.Message}"); }
            EndCall("pin-timeout");
            EnsureUserAgentHealthy();
        }
        catch (OperationCanceledException) { }   // call ended before the deadline
        catch (Exception ex)
        {
            Log.LogStep($"SIP PIN timeout handler error: {ex.Message}");
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

    // ─── STT/TTS via the persistent voice subprocess (see SipVoiceAgent.cs) ─

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

    // ─── Announcements (localization: Resources/Dictionary.*.resx — ALL 5 languages) ──
    //
    // LOCALIZATION INVARIANT (see the file header): never hardcode announcement text here.
    // The strings live in the dictionaries (SipAnnounce*) and are read with the culture of the
    // resolved call language. The old `Italian ? "…" : "…"` supported only 2 languages.

    /// <summary>Sets the dictionary culture to the resolved call language (it/de/es/fr/ru/…),
    /// so every <see cref="Lang"/> lookup returns the right language.</summary>
    private static void ApplyAnnouncementLanguage()
    {
        try { Lang.Culture = CultureInfo.GetCultureInfo(VoiceConversation.ResolveLang(Cfg.Lang)); }
        catch { Lang.Culture = null; }   // fall back to the thread's culture
    }

    private static string AnnounceWelcome() { ApplyAnnouncementLanguage(); return Lang.SipAnnounceWelcome; }

    private static string AnnounceWelcomeOk() { ApplyAnnouncementLanguage(); return Lang.SipAnnounceWelcomeOk; }

    private static string AnnouncePinWrong(int remaining)
    {
        ApplyAnnouncementLanguage();
        return string.Format(Lang.SipAnnouncePinWrong, remaining);
    }

    private static string AnnounceLocked() { ApplyAnnouncementLanguage(); return Lang.SipAnnounceLocked; }

    private static string AnnouncePinTimeout() { ApplyAnnouncementLanguage(); return Lang.SipAnnouncePinTimeout; }
}
