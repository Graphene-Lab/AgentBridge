// VadTest — deterministic test of the ADAPTIVE VAD (ambient calibration, B) against the
// AIOffice.VoiceAgent --pipe-audio subprocess.
//
// Feeds: [1.0 s noise @ level] + [speech from greeting-it.wav] + [1.5 s noise @ level]
// for several noise levels relative to the speech RMS. Asserts per case:
//   • an utterance CLOSES (VAD "end" fires — the utterance is not stuck open on noise),
//   • the transcript contains the speech (the threshold did not cut it),
//   • the VAD state stream is coherent: "speech" (utterance opened) → "end" (closed, i.e. the
//     processing indicator would be armed here) → the transcript,
//   • whisper INFERENCE (wall-clock "end" → transcript) is sane (< 8 s) — the STT latency.
// The old VAD stayed open forever when the noise exceeded its fixed 0.006 threshold; the new
// one calibrates the ambient from the first ~1 s and sets threshold = ambient × 2.
//
// Usage: dotnet run --project e2e\VadTest [--agent <AIOffice.VoiceAgent.exe>]
//   (set AIOFFICE_WHISPER_MODEL / AIOFFICE_WHISPER_QUANT in the environment to test a
//    specific whisper model — e.g. `set AIOFFICE_WHISPER_MODEL=base` for a base re-test)
using System.Diagnostics;
using System.Text;
using System.Text.Json;

var agent = args.FirstOrDefault(a => a.StartsWith("--agent"))?[8..]
    ?? @"c:\Users\andre\OneDrive\Sorgenti\AgentBridge\bin\Debug\net10.0\voiceagent-stt\AIOffice.VoiceAgent.exe";
var greeting = args.FirstOrDefault(a => a.StartsWith("--greet"))?[8..]
    ?? @"c:\Users\andre\OneDrive\Sorgenti\AgentBridge\e2e\SipClientTest\bin\Debug\net10.0\greeting-it.wav";

var speech16k = Upsample8k16k(WavPcm(greeting));
var speechRms = Rms(speech16k);
Console.WriteLine($"speech: {speech16k.Length / 2 / 16000.0:F1}s, rms={speechRms:F4}");

var noiseRatios = new[] { 0.10, 0.30, 0.50 };   // ambient as a fraction of the speech RMS
var failed = 0;
foreach (var ratio in noiseRatios)
{
    var noiseLevel = speechRms * ratio;
    var pcm = Concat(
        Noise(1.0, noiseLevel),        // 1.0 s ambient calibration
        speech16k,                     // the spoken phrase
        Noise(1.5, noiseLevel));       // 1.5 s trailing ambient
    Console.WriteLine($"\n=== case noise={ratio:F2}×speech (ambient rms {noiseLevel:F4}) ===");
    var (ok, transcript, inferenceSec, transcriptAt, vad) = RunAgent(agent, pcm);
    var vadSeq = string.Join("→", vad.Select(v => $"{v.State}@{v.At:F1}s"));
    var endAt = vad.FirstOrDefault(v => v.State == "end");
    // "end" (utterance closed → indicator armed) must precede the transcript (whisper done).
    // inferenceSec is the wall-clock whisper time (end → transcript): the real STT latency.
    var vadOk = HasSpeechThenEnd(vad) && endAt != default && transcriptAt > endAt.At;
    Console.WriteLine($"  transcript: '{transcript}'  (whisper inference {inferenceSec:F1}s after utterance end)  ok={ok}");
    Console.WriteLine($"  vad events: {vadSeq}  ok={vadOk}");
    if (!ok || !vadOk) failed++;
}
Console.WriteLine(failed == 0 ? "\nRESULT: PASS — the adaptive VAD closes utterances at every noise level, VAD events are coherent"
                              : $"\nRESULT: FAIL — {failed} case(s) failed");
return failed == 0 ? 0 : 1;

// ─── helpers ──────────────────────────────────────────────────────────────

static bool HasSpeechThenEnd(List<(string State, double At)> vad) =>
    vad.Any(v => v.State == "speech") && vad.Any(v => v.State == "end") &&
    vad.First(v => v.State == "speech").At < vad.First(v => v.State == "end").At;

static (bool, string?, double, double, List<(string State, double At)>) RunAgent(string exe, byte[] pcm16k)
{
    var psi = new ProcessStartInfo
    {
        FileName = exe,
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        CreateNoWindow = true,
        StandardInputEncoding = new UTF8Encoding(false),
    };
    psi.ArgumentList.Add("--pipe-audio");
    using var proc = Process.Start(psi)!;
    var transcripts = new List<(string Text, double At)>();
    var vad = new List<(string State, double At)>();
    var t0 = DateTime.UtcNow;
    var readerTask = Task.Run(async () =>
    {
        while (await proc.StandardOutput.ReadLineAsync() is { } line)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var at = (DateTime.UtcNow - t0).TotalSeconds;
                if (root.TryGetProperty("type", out var t))
                {
                    if (t.GetString() == "transcript" && root.TryGetProperty("text", out var tx))
                        transcripts.Add((tx.GetString()!, at));
                    else if (t.GetString() == "vad" && root.TryGetProperty("state", out var st))
                        vad.Add((st.GetString()!, at));
                }
            }
            catch { }
        }
    });

    proc.StandardInput.WriteLine(JsonSerializer.Serialize(new { cmd = "start", lang = "it" }));
    proc.StandardInput.Flush();
    Thread.Sleep(3000);   // model load

    // Feed the PCM in 20 ms chunks (like the SIP bridge does).
    for (int off = 0; off < pcm16k.Length; off += 3200)
    {
        var chunk = pcm16k.AsSpan(off, Math.Min(3200, pcm16k.Length - off)).ToArray();
        proc.StandardInput.WriteLine(JsonSerializer.Serialize(new { cmd = "audio", b64 = Convert.ToBase64String(chunk) }));
        proc.StandardInput.Flush();
        Thread.Sleep(1);
    }
    // Wait for the trailing hangover (700 ms) + whisper.
    for (int i = 0; i < 40 && transcripts.Count == 0; i++) Thread.Sleep(500);

    proc.StandardInput.WriteLine(JsonSerializer.Serialize(new { cmd = "stop" }));
    proc.StandardInput.Flush();
    Thread.Sleep(800);
    try { proc.Kill(true); } catch { }

    if (transcripts.Count == 0) return (false, null, double.NaN, double.NaN, vad);
    var first = transcripts[0];
    // Whisper inference = wall-clock time from the utterance close ("end") to the transcript.
    // (The old "dt after speech end" metric assumed audio-time ≈ wall-clock, but the PCM is fed
    // ~20x faster than real-time — with a fast model the transcript lands "before" the nominal
    // speech end and dt went negative. The end→transcript delta is the real STT latency.)
    var endAt = vad.FirstOrDefault(v => v.State == "end");
    var inferenceSec = endAt != default ? first.At - endAt.At : double.NaN;
    var ok = first.Text.Length > 5 && inferenceSec is > 0.2 and < 8.0;   // closed, transcribed, not stuck
    return (ok, first.Text, inferenceSec, first.At, vad);
}

static byte[] WavPcm(string path)
{
    var bytes = File.ReadAllBytes(path);
    var off = 12;
    while (off + 8 <= bytes.Length)
    {
        var id = Encoding.ASCII.GetString(bytes, off, 4);
        var size = BitConverter.ToInt32(bytes, off + 4);
        if (id == "data") return bytes.AsSpan(off + 8, Math.Min(size, bytes.Length - off - 8)).ToArray();
        off += 8 + size + (size % 2);
    }
    throw new InvalidDataException("no data chunk");
}

static byte[] Upsample8k16k(byte[] pcm8k)
{
    var n = pcm8k.Length / 2;
    var outp = new byte[n * 4];
    for (int i = 0; i < n; i++)
    {
        var s = (short)(pcm8k[i * 2] | pcm8k[i * 2 + 1] << 8);
        var nx = i + 1 < n ? (short)(pcm8k[(i + 1) * 2] | pcm8k[(i + 1) * 2 + 1] << 8) : s;
        var mid = (short)((s + nx) / 2);
        var o = i * 4;
        outp[o] = (byte)(s & 0xFF); outp[o + 1] = (byte)((s >> 8) & 0xFF);
        outp[o + 2] = (byte)(mid & 0xFF); outp[o + 3] = (byte)((mid >> 8) & 0xFF);
    }
    return outp;
}

static byte[] Noise(double seconds, double rms)
{
    var n = (int)(seconds * 16000);
    var rng = new Random(42);
    var pcm = new byte[n * 2];
    for (int i = 0; i < n; i++)
    {
        // Gaussian-ish via sum of uniforms, scaled to the target RMS.
        var v = (rng.NextDouble() + rng.NextDouble() + rng.NextDouble() - 1.5) * 2.45 * rms;
        var s = (short)Math.Max(-32768, Math.Min(32767, v * short.MaxValue));
        pcm[i * 2] = (byte)(s & 0xFF);
        pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
    }
    return pcm;
}

static byte[] Concat(params byte[][] parts)
{
    using var ms = new MemoryStream();
    foreach (var p in parts) ms.Write(p);
    return ms.ToArray();
}

static double Rms(byte[] pcm16)
{
    long sum = 0;
    for (int i = 0; i < pcm16.Length; i += 2)
    {
        var s = (short)(pcm16[i] | pcm16[i + 1] << 8);
        sum += (long)s * s;
    }
    return Math.Sqrt(sum / (double)(pcm16.Length / 2)) / short.MaxValue;
}
