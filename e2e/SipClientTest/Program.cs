//  SipClientTest — minimal SIP test client for the AgentBridge voice chain.
//
//  Dials a SIP URI through the entry point (or directly), answers the spoken PIN prompt
//  with DTMF, then speaks a TTS greeting (bundled greeting-it.wav, or --greet=file.wav)
//  and captures whatever the agent replies over RTP. The reply is saved as reply.wav in
//  the current directory. The agent-side truth is the server log ("SIP caller said: …").
//
//  Usage:
//    dotnet run --project e2e\SipClientTest [uri] [pin] [local-port] [hold-seconds] [options]
//    dotnet run --project e2e\SipClientTest                        # sip:agent@195.20.235.5, 12345, 5071, 25 s
//
//  Options:
//    --dtmf=info     send the PIN via SIP INFO (application/dtmf-relay) instead of RFC 4733
//    --dtmf=inband   send the PIN as in-band keypad tones (Goertzel-detected by the agent)
//    --no-pin        do not send any PIN (exercises the agent's PIN timeout)
//    --greet=file.wav  greeting WAV (8 kHz PCM16) instead of the bundled one
//    --pin-wait=N    seconds to wait after the PIN before speaking (default 10)
//
//  Exit code 0 = call answered, reply speech received back over RTP.
// ═══════════════════════════════════════════════════════════════════════
using System.Net;
using System.Text;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

var uri = args.Length > 0 ? args[0] : "sip:agent@195.20.235.5";
var pin = args.Length > 1 ? args[1] : "12345";
var localPort = args.Length > 2 ? int.Parse(args[2]) : 5071;   // non-5060: home ISPs drop inbound UDP 5060
var holdSeconds = args.Length > 3 ? int.Parse(args[3]) : 25;
var pinWaitArg = args.FirstOrDefault(a => a.StartsWith("--pin-wait"));
var pinWaitSeconds = pinWaitArg != null ? int.Parse(pinWaitArg[(pinWaitArg.IndexOf('=') + 1)..]) : 10;
var greetArg = args.FirstOrDefault(a => a.StartsWith("--greet"));
var greetPath = greetArg != null ? greetArg[(greetArg.IndexOf('=') + 1)..]
                                 : Path.Combine(AppContext.BaseDirectory, "greeting-it.wav");
var dtmfMode = args.FirstOrDefault(a => a.StartsWith("--dtmf="))?[7..] ?? "rfc4733";
var noPin = args.Contains("--no-pin");
var replyPath = Path.Combine(Environment.CurrentDirectory, "reply.wav");

Console.WriteLine($"SipClientTest → {uri}  pin={pin}  local udp {localPort}  hold {holdSeconds}s  dtmf={dtmfMode}{(noPin ? " (no-pin)" : "")}");

using var transport = new SIPTransport();
transport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, localPort)));

// G.711 only, no on-hold music generator (the parameterless VoIPMediaSession ctor would
// stream hold music to the agent and pollute its STT).
var endpoint = new MediaEndPoints { AudioSource = new AudioExtrasSource() };
var media = new VoIPMediaSession(new VoIPMediaSessionConfig { MediaEndPoint = endpoint });
endpoint.AudioSource!.RestrictFormats(f => f.Codec == AudioCodecsEnum.PCMU || f.Codec == AudioCodecsEnum.PCMA);
media.AcceptRtpFromAny = true;

// Capture whatever the agent sends back (G.711 payloads; RFC 4733 DTMF events skipped).
var received = new MemoryStream();
var audioSync = new object();
double peakRms = 0;
media.OnRtpPacketReceived += (_, _, packet) =>
{
    var payload = packet.Payload;
    if (payload == null || payload.Length == 0) return;
    var pt = packet.Header.PayloadType;
    if (pt != 0 && pt != 8) return;
    lock (audioSync)
    {
        long sumSquares = 0;
        for (int i = 0; i < payload.Length; i++)
        {
            var sample = pt == 0
                ? MuLawDecoder.MuLawToLinearSample(payload[i])
                : ALawDecoder.ALawToLinearSample(payload[i]);
            received.WriteByte((byte)(sample & 0xFF));
            received.WriteByte((byte)((sample >> 8) & 0xFF));
            sumSquares += (long)sample * sample;
        }
        var rms = Math.Sqrt(sumSquares / (double)payload.Length) / short.MaxValue;
        if (rms > peakRms) peakRms = rms;
    }
};

var answered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
SIPResponse? answerResp = null;
var ua = new SIPUserAgent(transport, null);
ua.ClientCallAnswered += (_, resp) =>
{
    Console.WriteLine($"[client] answered: {resp.Status} {resp.ReasonPhrase}");
    if (resp.Status == SIPResponseStatusCodesEnum.Ok) { answerResp = resp; answered.TrySetResult(true); }
};
ua.ClientCallFailed += (_, err, _) => { Console.WriteLine($"[client] call failed: {err}"); answered.TrySetResult(false); };
ua.OnCallHungup += _ => Console.WriteLine("[client] call ended by the remote");

Console.WriteLine($"[client] calling {uri} …");
var callOk = await ua.Call(uri, null, null, media, ringTimeout: 0).WaitAsync(TimeSpan.FromSeconds(40));
if (!callOk || !await answered.Task)
{
    Console.WriteLine("FAIL: call not answered (check registration / entry point / agent)");
    return 2;
}

// 1. Spoken welcome is playing — 2.5 s in, send the PIN with the selected method. In-band
//    tones need more time: the agent pauses capture while its TTS plays, so they are sent
//    after the welcome ends (~6.5 s).
await Task.Delay(dtmfMode == "inband" ? 6500 : 2500);
if (noPin)
{
    Console.WriteLine($"[client] --no-pin: sending nothing (the agent should time out)");
}
else if (dtmfMode == "info")
{
    Console.WriteLine($"[client] sending PIN {pin} via SIP INFO");
    foreach (var ch in pin)
    {
        SendInfoDtmf(transport, answerResp, uri, ch);
        await Task.Delay(300);
    }
}
else if (dtmfMode == "inband")
{
    Console.WriteLine($"[client] sending PIN {pin} as in-band tones");
    var tones = GenerateDtmfTones(pin);
    await media.AudioExtrasSource.SendAudioFromStream(new MemoryStream(tones), AudioSamplingRatesEnum.Rate8KHz);
}
else
{
    Console.WriteLine($"[client] sending PIN {pin} (RFC 4733)");
    foreach (var ch in pin)
    {
        await ua.SendDtmf((byte)(ch - '0'));
        await Task.Delay(250);
    }
}

// 2. PIN validation + "connecting" announcement — wait out the spoken prompts (default 10 s
//    after the PIN: the conversation phase starts only once the agent has accepted it), then
//    speak the greeting so it lands in the live conversation.
await Task.Delay(TimeSpan.FromSeconds(pinWaitSeconds));
var greetingPcm = LoadPcmDataChunk(greetPath);
if (greetingPcm == null)
{
    Console.WriteLine($"WARN: greeting wav not found/readable ({greetPath}) — skipping speech");
}
else
{
    Console.WriteLine($"[client] speaking greeting ({greetingPcm.Length / 16} ms)");
    await media.AudioExtrasSource.SendAudioFromStream(new MemoryStream(greetingPcm), AudioSamplingRatesEnum.Rate8KHz);
}

// 3. Listen for the agent's reply until the hold time elapses.
var deadline = DateTime.UtcNow.AddSeconds(holdSeconds);
var lastLog = DateTime.UtcNow;
while (DateTime.UtcNow < deadline && ua.IsCallActive)
{
    if ((DateTime.UtcNow - lastLog).TotalSeconds >= 5)
    {
        lastLog = DateTime.UtcNow;
        lock (audioSync) Console.WriteLine($"[client] listening… {received.Length / 16} ms audio, peak rms {peakRms:F3}");
    }
    await Task.Delay(500);
}

lock (audioSync) Console.WriteLine($"[client] done — {received.Length / 16} ms audio received, peak rms {peakRms:F3}");
if (received.Length > 0) WriteWav(replyPath, received.ToArray());
Console.WriteLine($"[client] reply audio saved to {replyPath}");

Console.WriteLine("[client] hanging up");
ua.Hangup();
await Task.Delay(500);

var ok = peakRms > 0.02;
Console.WriteLine(ok ? "RESULT: PASS — the agent spoke back"
                     : "RESULT: FAIL — no speech received back (check agent log for 'SIP caller said')");
return ok ? 0 : 1;

/// <summary>Extracts the raw PCM16 bytes of the "data" chunk from a WAV (any header layout,
/// e.g. SAPI's extra LIST chunks), 8 kHz mono expected.</summary>
static byte[]? LoadPcmDataChunk(string path)
{
    try
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 44 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF") return null;
        var offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var id = Encoding.ASCII.GetString(bytes, offset, 4);
            var size = BitConverter.ToInt32(bytes, offset + 4);
            if (id == "data") return bytes.AsSpan(offset + 8, Math.Min(size, bytes.Length - offset - 8)).ToArray();
            offset += 8 + size + (size % 2);
        }
        return null;
    }
    catch
    {
        return null;
    }
}

static void WriteWav(string path, byte[] pcm)
{
    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);
    w.Write("RIFF"u8);
    w.Write(36 + pcm.Length);
    w.Write("WAVE"u8);
    w.Write("fmt "u8);
    w.Write(16);
    w.Write((short)1);
    w.Write((short)1);
    w.Write(8000);
    w.Write(16000);
    w.Write((short)2);
    w.Write((short)16);
    w.Write("data"u8);
    w.Write(pcm.Length);
    w.Write(pcm);
}

/// <summary>Sends one DTMF digit as an in-dialog SIP INFO (application/dtmf-relay), reusing
/// the dialog identifiers from the 200 OK of the established call.</summary>
static void SendInfoDtmf(SIPTransport transport, SIPResponse? answer, string callUri, char digit)
{
    if (answer == null) return;
    var uri = SIPURI.TryParse(callUri, out var parsed) ? parsed : answer.Header.To.ToURI;
    var info = SIPRequest.GetRequest(SIPMethodsEnum.INFO, uri, answer.Header.To, answer.Header.From);
    info.Header.CallId = answer.Header.CallId;
    info.Header.CSeq = answer.Header.CSeq + 1;
    info.Header.MaxForwards = 70;
    // In-dialog request: the proxy (entry point) requires the Route set recorded on the call
    // (Record-Route from the 200 OK) or it answers 404 Not Here (loose_route fails).
    if (answer.Header.RecordRoutes is { Length: > 0 } rr)
        for (int i = 0; i < rr.Length; i++)
            info.Header.Routes.AddBottomRoute(rr.GetAt(i));
    info.Header.ContentType = "application/dtmf-relay";
    info.Body = $"Signal = {digit}\r\n";
    Console.WriteLine($"[client] INFO wire: {info.ToString().Split('\n')[0]} via={info.Header.Vias.TopViaHeader?.ToString()?.Split(';')[0]} routes={info.Header.Routes.Length} firstRoute={info.Header.Routes.TopRoute?.ToString()}");
    var tx = new SIPNonInviteTransaction(transport, info, null);
    tx.NonInviteTransactionFinalResponseReceived += (_, _, _, resp) =>
    {
        Console.WriteLine($"[client] INFO {digit} → {resp.Status}");
        return Task.FromResult(System.Net.Sockets.SocketError.Success);
    };
    tx.SendRequest();
}

/// <summary>Synthesises the PIN as in-band DTMF tone pairs (80 ms tone + 60 ms gap per digit,
/// summed row+column frequencies at 8 kHz PCM16) — the agent detects these with Goertzel.</summary>
static byte[] GenerateDtmfTones(string digits)
{
    const int sampleRate = 8000;
    var freqs = new Dictionary<char, (double, double)>
    {
        { '1', (697, 1209) }, { '2', (697, 1336) }, { '3', (697, 1477) },
        { '4', (770, 1209) }, { '5', (770, 1336) }, { '6', (770, 1477) },
        { '7', (852, 1209) }, { '8', (852, 1336) }, { '9', (852, 1477) },
        { '*', (941, 1209) }, { '0', (941, 1336) }, { '#', (941, 1477) },
    };
    using var ms = new MemoryStream();
    foreach (var ch in digits)
    {
        var (f1, f2) = freqs[ch];
        var toneSamples = sampleRate * 80 / 1000;
        var gapSamples = sampleRate * 60 / 1000;
        for (int i = 0; i < toneSamples; i++)
        {
            var t = i / (double)sampleRate;
            var s = (short)(0.25 * short.MaxValue * (Math.Sin(2 * Math.PI * f1 * t) + Math.Sin(2 * Math.PI * f2 * t)));
            ms.WriteByte((byte)(s & 0xFF));
            ms.WriteByte((byte)((s >> 8) & 0xFF));
        }
        for (int i = 0; i < gapSamples; i++) { ms.WriteByte(0); ms.WriteByte(0); }
    }
    return ms.ToArray();
}
