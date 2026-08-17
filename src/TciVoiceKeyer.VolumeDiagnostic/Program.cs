using System.Buffers.Binary;
using System.Text;
using TciVoiceKeyer.Engine;

Console.WriteLine("TCI Voice Keyer - WAV volume diagnostic");
Console.WriteLine("WARNING: this test WILL transmit the selected WAV once.");
Console.WriteLine("Volume is applied to the WAV samples before the proven TCI keyer engine transmits them.\n");

if (args.Length != 3 ||
    !Uri.TryCreate(args[0], UriKind.Absolute, out var serverUri) ||
    !double.TryParse(args[2], out var volumePercent))
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project src/TciVoiceKeyer.VolumeDiagnostic -- ws://<thetis-ip>:<port> \"C:\\path\\message.wav\" <volume-percent>");
    Console.WriteLine("Examples:");
    Console.WriteLine("  ... \"C:\\audio\\cq.wav\" 50");
    Console.WriteLine("  ... \"C:\\audio\\cq.wav\" 100");
    Console.WriteLine("  ... \"C:\\audio\\cq.wav\" 150");
    return;
}

if (volumePercent is < 0 or > 200)
{
    Console.WriteLine("Volume percent must be between 0 and 200.");
    return;
}

var sourcePath = Path.GetFullPath(args[1]);
if (!File.Exists(sourcePath))
{
    Console.WriteLine($"WAV file not found: {sourcePath}");
    return;
}

var gain = volumePercent / 100.0;
var tempPath = Path.Combine(Path.GetTempPath(), $"tci-keyer-volume-{Guid.NewGuid():N}.wav");

try
{
    var result = CreateScaledFloatWav(sourcePath, tempPath, gain);

    Console.WriteLine($"Source WAV:    {Path.GetFileName(sourcePath)}");
    Console.WriteLine($"Volume:        {volumePercent:F0}%");
    Console.WriteLine($"Linear gain:   {gain:F3}x");
    Console.WriteLine($"Source peak:   {result.SourcePeak:F3} ({ToDbfs(result.SourcePeak):F1} dBFS)");
    Console.WriteLine($"Scaled peak:   {result.OutputPeak:F3} ({ToDbfs(result.OutputPeak):F1} dBFS)");
    Console.WriteLine($"Clipped frames:{result.ClippedFrames}");
    if (result.ClippedFrames > 0)
        Console.WriteLine("WARNING: this volume setting would exceed full scale; samples have been limited to +/-1.0. Try a lower setting for clean audio.");

    Console.WriteLine("\nThe proven engine will still apply its normal 20 ms fade and 500 ms automatic TX tail silence.");
    Console.WriteLine("Nothing has been transmitted yet.");
    Console.WriteLine("Type exactly VOLUME and press Enter to transmit this adjusted WAV ONCE.");
    Console.Write("Confirmation: ");
    if (!string.Equals(Console.ReadLine(), "VOLUME", StringComparison.Ordinal))
    {
        Console.WriteLine("Cancelled. No PTT command was sent.");
        return;
    }

    var options = new KeyerOptions(
        serverUri,
        tempPath,
        1,
        TimeSpan.Zero,
        KeyerOptions.DefaultTailSilence);

    await using var keyer = new VoiceKeyerEngine();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Console.WriteLine("\nCtrl+C: STOP requested; forcing RX...");
        _ = keyer.StopAsync();
    };

    keyer.StatusChanged += status =>
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} {status.State}: {status.Message}");

    await keyer.StartAsync(options);
}
catch (Exception ex)
{
    Console.WriteLine($"ERROR: {ex.Message}");
    Environment.ExitCode = 1;
}
finally
{
    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
}

Console.WriteLine("Diagnostic finished. Confirm Thetis is in RX.");

static double ToDbfs(float peak) => 20.0 * Math.Log10(Math.Max(peak, 1e-12f));

static ScaleResult CreateScaledFloatWav(string sourcePath, string outputPath, double gain)
{
    using var fs = File.OpenRead(sourcePath);
    using var br = new BinaryReader(fs);
    if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "RIFF") throw new InvalidDataException("Not a RIFF WAV file.");
    br.ReadUInt32();
    if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "WAVE") throw new InvalidDataException("Not a WAVE file.");

    ushort format = 0, channels = 0, bits = 0;
    uint rate = 0;
    byte[]? data = null;
    while (fs.Position + 8 <= fs.Length)
    {
        var id = Encoding.ASCII.GetString(br.ReadBytes(4));
        var size = br.ReadUInt32();
        var next = fs.Position + size;
        if (id == "fmt ")
        {
            format = br.ReadUInt16();
            channels = br.ReadUInt16();
            rate = br.ReadUInt32();
            br.ReadUInt32(); br.ReadUInt16(); bits = br.ReadUInt16();
        }
        else if (id == "data")
            data = br.ReadBytes(checked((int)size));
        fs.Position = next + (size % 2);
    }

    if (data is null) throw new InvalidDataException("No data chunk found.");
    if (rate != 48000) throw new InvalidDataException($"Volume diagnostic requires 48000 Hz; found {rate} Hz.");
    if (channels is < 1 or > 2) throw new InvalidDataException($"Only mono/stereo WAV is supported; found {channels} channels.");
    if (format != 1 && format != 3) throw new InvalidDataException($"Only PCM (1) or IEEE float (3) WAV is supported; found format {format}.");
    if (format == 3 && bits != 32) throw new InvalidDataException("IEEE float WAV must be 32-bit.");
    if (format == 1 && bits is not (16 or 24 or 32)) throw new InvalidDataException($"PCM bit depth {bits} is not supported.");

    var bytesPerSample = bits / 8;
    var frameBytes = bytesPerSample * channels;
    var frames = data.Length / frameBytes;
    var mono = new float[frames];
    float sourcePeak = 0, outputPeak = 0;
    var clipped = 0;

    for (var i = 0; i < frames; i++)
    {
        double sum = 0;
        for (var ch = 0; ch < channels; ch++)
        {
            var o = i * frameBytes + ch * bytesPerSample;
            float v;
            if (format == 3) v = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(o, 4)));
            else if (bits == 16) v = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(o, 2)) / 32768f;
            else if (bits == 24)
            {
                var raw = data[o] | (data[o + 1] << 8) | (data[o + 2] << 16);
                if ((raw & 0x800000) != 0) raw |= unchecked((int)0xFF000000);
                v = raw / 8388608f;
            }
            else v = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(o, 4)) / 2147483648f;
            sum += v;
        }

        var source = (float)Math.Clamp(sum / channels, -1.0, 1.0);
        sourcePeak = Math.Max(sourcePeak, Math.Abs(source));
        var scaledRaw = source * gain;
        if (scaledRaw > 1.0 || scaledRaw < -1.0) clipped++;
        var scaled = (float)Math.Clamp(scaledRaw, -1.0, 1.0);
        mono[i] = scaled;
        outputPeak = Math.Max(outputPeak, Math.Abs(scaled));
    }

    using var outFs = File.Create(outputPath);
    using var bw = new BinaryWriter(outFs);
    var dataBytes = mono.Length * 4;
    bw.Write(Encoding.ASCII.GetBytes("RIFF"));
    bw.Write(36 + dataBytes);
    bw.Write(Encoding.ASCII.GetBytes("WAVE"));
    bw.Write(Encoding.ASCII.GetBytes("fmt "));
    bw.Write(16);
    bw.Write((ushort)3); // IEEE float
    bw.Write((ushort)1); // mono
    bw.Write(48000);
    bw.Write(48000 * 4);
    bw.Write((ushort)4);
    bw.Write((ushort)32);
    bw.Write(Encoding.ASCII.GetBytes("data"));
    bw.Write(dataBytes);
    foreach (var sample in mono) bw.Write(sample);

    return new ScaleResult(sourcePeak, outputPeak, clipped);
}

sealed record ScaleResult(float SourcePeak, float OutputPeak, int ClippedFrames);
