// One-shot asset converter: MP3 → 24 kHz mono 16-bit WAV (the processing indicator).
// Usage: Mp3ToWav <in.mp3> <out.wav> [trimSeconds]   — trims to the first N seconds (loop).
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

var src = args[0];
var dst = args[1];
var trimSec = args.Length > 2 ? double.Parse(args[2]) : 0;
using var reader = new Mp3FileReader(src);
ISampleProvider samples = new Pcm16BitToSampleProvider(reader);
if (reader.WaveFormat.Channels > 1) samples = new StereoToMonoSampleProvider(samples);
if (reader.WaveFormat.SampleRate != 24000) samples = new WdlResamplingSampleProvider(samples, 24000);
using var writer = new WaveFileWriter(dst, new WaveFormat(24000, 16, 1));
var pcm16 = new SampleToWaveProvider16(samples);
var buf = new byte[8192];
long total = 0;
long budget = trimSec > 0 ? (long)(trimSec * 48000) : long.MaxValue;
int read;
while (total < budget && (read = pcm16.Read(buf, 0, (int)Math.Min(buf.Length, budget - total))) > 0)
{
    writer.Write(buf, 0, read);
    total += read;
}
Console.WriteLine($"converted {total / 48000.0:F2} s @ 24kHz mono");
