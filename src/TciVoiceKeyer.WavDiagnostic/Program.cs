using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;

const int Transceiver = 0;
const int StreamHeaderBytes = 64;
const uint TxAudioStream = 2;
const uint TxChrono = 3;
const int RequiredRate = 48000;
const int MaxTransmitSeconds = 30;
const int TailSilenceMilliseconds = 250;

Console.WriteLine("TCI Voice Keyer - Milestone 5 WAV playback diagnostic");
Console.WriteLine("WARNING: this test WILL transmit the selected WAV file over TCI.");
Console.WriteLine("For this milestone the WAV must be 48 kHz PCM/float, mono or stereo.");
Console.WriteLine($"After the WAV ends, {TailSilenceMilliseconds} ms of TCI-fed digital silence is sent before unkeying.");
Console.WriteLine("Use USB mode and make sure transmitting the recording is safe.\n");

if (args.Length != 2 || !Uri.TryCreate(args[0], UriKind.Absolute, out var serverUri) ||
    (serverUri.Scheme != "ws" && serverUri.Scheme != "wss"))
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project src/TciVoiceKeyer.WavDiagnostic -- ws://<thetis-ip>:<tci-port> \"C:\\path\\message.wav\"");
    return;
}

var wavPath = Path.GetFullPath(args[1]);
if (!File.Exists(wavPath))
{
    Console.WriteLine($"WAV file not found: {wavPath}");
    return;
}

WavData wav;
try
{
    wav = LoadWav(wavPath);
}
catch (Exception ex)
{
    Console.WriteLine($"WAV validation failed: {ex.Message}");
    return;
}

if (wav.SampleRate != RequiredRate)
{
    Console.WriteLine($"WAV sample rate is {wav.SampleRate} Hz. Milestone 5 requires exactly {RequiredRate} Hz.");
    Console.WriteLine("No transmission attempted.");
    return;
}

if (wav.Duration.TotalSeconds > MaxTransmitSeconds)
{
    Console.WriteLine($"WAV duration is {wav.Duration.TotalSeconds:F1}s; safety limit is {MaxTransmitSeconds}s.");
    return;
}

Console.WriteLine($"Loaded WAV: {Path.GetFileName(wavPath)}");
Console.WriteLine($"  Rate:      {wav.SampleRate} Hz");
Console.WriteLine($"  Channels:  {wav.SourceChannels}");
Console.WriteLine($"  Samples:   {wav.MonoSamples.Length}");
Console.WriteLine($"  Duration:  {wav.Duration.TotalSeconds:F2} s");
Console.WriteLine($"  Peak:      {wav.Peak:F3} ({20 * Math.Log10(Math.Max(wav.Peak, 1e-12)):F1} dBFS)\n");

using var socket = new ClientWebSocket();
var txAllowed = false;
var usbMode = false;
var readySeen = false;
var transmitCommandSent = false;
var sampleIndex = 0;
var txChronoCount = 0;
var audioPacketCount = 0;
var tailSilencePacketCount = 0;
var rxConfirmed = false;

async Task SendTextAsync(string command)
{
    var bytes = Encoding.UTF8.GetBytes(command);
    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    Console.WriteLine($"SEND  {command}");
}

async Task TryUnkeyAsync()
{
    if (socket.State != WebSocketState.Open)
    {
        Console.WriteLine($"WARNING: cannot unkey because WebSocket state is {socket.State}.");
        Console.WriteLine("CHECK THETIS IMMEDIATELY AND ENSURE MOX/PTT IS OFF.");
        return;
    }

    try
    {
        await SendTextAsync($"trx:{Transceiver},false;");
        transmitCommandSent = false;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"WARNING: automatic unkey failed: {ex.Message}");
        Console.WriteLine("CHECK THETIS IMMEDIATELY AND ENSURE MOX/PTT IS OFF.");
    }
}

async Task<(WebSocketMessageType Type, byte[] Payload)> ReceiveMessageAsync()
{
    var receiveBuffer = new byte[64 * 1024];
    using var messageBuffer = new MemoryStream();
    WebSocketReceiveResult result;
    do
    {
        result = await socket.ReceiveAsync(receiveBuffer, CancellationToken.None);
        if (result.MessageType == WebSocketMessageType.Close)
            return (WebSocketMessageType.Close, Array.Empty<byte>());
        messageBuffer.Write(receiveBuffer, 0, result.Count);
    }
    while (!result.EndOfMessage);
    return (result.MessageType, messageBuffer.ToArray());
}

try
{
    Console.WriteLine($"Connecting to {serverUri} ...");
    await socket.ConnectAsync(serverUri, CancellationToken.None);
    Console.WriteLine("Connected. Waiting for READY, USB mode and TX permission...\n");

    while (socket.State == WebSocketState.Open && !readySeen)
    {
        var message = await ReceiveMessageAsync();
        if (message.Type == WebSocketMessageType.Close)
            throw new InvalidOperationException("Thetis closed the WebSocket during initialization.");
        if (message.Type != WebSocketMessageType.Text)
            continue;

        var text = Encoding.UTF8.GetString(message.Payload);
        Console.WriteLine($"TEXT  {text}");
        if (text.Contains($"tx_enable:{Transceiver},true;", StringComparison.OrdinalIgnoreCase)) txAllowed = true;
        if (text.Contains($"modulation:{Transceiver},USB;", StringComparison.OrdinalIgnoreCase)) usbMode = true;
        if (text.Contains("ready;", StringComparison.OrdinalIgnoreCase)) readySeen = true;
    }

    if (!txAllowed)
    {
        Console.WriteLine("ABORTED: TX is not enabled for transceiver 0.");
        return;
    }
    if (!usbMode)
    {
        Console.WriteLine("ABORTED: receiver 0 is not in USB mode. No PTT command was sent.");
        return;
    }

    Console.WriteLine("\nConfiguring proven TCI TX audio format:");
    await SendTextAsync("audio_samplerate:48000;");
    await SendTextAsync("audio_stream_sample_type:float32;");
    await SendTextAsync("audio_stream_channels:2;");
    await SendTextAsync("audio_stream_samples:512;");
    await SendTextAsync("tx_stream_audio_buffering:100;");

    Console.WriteLine("\nNothing has been transmitted yet.");
    Console.WriteLine($"Ready to transmit: {Path.GetFileName(wavPath)} ({wav.Duration.TotalSeconds:F2}s)");
    Console.WriteLine("Type exactly WAV and press Enter to transmit it ONCE.");
    Console.Write("Confirmation: ");
    if (!string.Equals(Console.ReadLine(), "WAV", StringComparison.Ordinal))
    {
        Console.WriteLine("Cancelled. No PTT command was sent.");
        return;
    }

    Console.WriteLine("\nFinal countdown:");
    for (var i = 3; i >= 1; i--) { Console.WriteLine($"  {i}..."); await Task.Delay(1000); }

    Console.WriteLine("\nKEYING NOW - WAV playback");
    await SendTextAsync($"trx:{Transceiver},true,tci;");
    transmitCommandSent = true;

    var hardStop = DateTime.UtcNow.AddSeconds(MaxTransmitSeconds + 3);
    var pendingReceive = ReceiveMessageAsync();
    var transmissionFinished = false;
    DateTime? tailSilenceUntil = null;

    while (!transmissionFinished && DateTime.UtcNow < hardStop && socket.State == WebSocketState.Open)
    {
        var message = await pendingReceive;
        if (message.Type == WebSocketMessageType.Close) throw new InvalidOperationException("Thetis closed connection during TX.");

        if (message.Type == WebSocketMessageType.Text)
        {
            var text = Encoding.UTF8.GetString(message.Payload);
            Console.WriteLine($"TEXT  {text}");
        }
        else if (message.Type == WebSocketMessageType.Binary && message.Payload.Length >= StreamHeaderBytes)
        {
            var receiver = ReadU32(message.Payload, 0);
            var sampleRate = ReadU32(message.Payload, 4);
            var sampleType = ReadU32(message.Payload, 8);
            var length = ReadU32(message.Payload, 20);
            var streamType = ReadU32(message.Payload, 24);
            var channels = ReadU32(message.Payload, 28);

            if (streamType == TxChrono)
            {
                txChronoCount++;
                if (receiver != 0 || sampleRate != 48000 || sampleType != 3 || channels != 2 || length == 0 || length > 4096 || length % 2 != 0)
                    throw new InvalidOperationException($"Unexpected TX_CHRONO: receiver={receiver}, rate={sampleRate}, type={sampleType}, length={length}, channels={channels}");

                var valuesRequested = checked((int)length);
                var framesRequested = valuesRequested / 2;
                var packet = new byte[StreamHeaderBytes + valuesRequested * 4];
                WriteU32(packet, 0, receiver); WriteU32(packet, 4, sampleRate); WriteU32(packet, 8, 3);
                WriteU32(packet, 12, 0); WriteU32(packet, 16, 0); WriteU32(packet, 20, length);
                WriteU32(packet, 24, TxAudioStream); WriteU32(packet, 28, 2);

                var packetIsTailSilence = sampleIndex >= wav.MonoSamples.Length;

                for (var frame = 0; frame < framesRequested; frame++)
                {
                    var value = sampleIndex < wav.MonoSamples.Length ? wav.MonoSamples[sampleIndex++] : 0f;
                    var bits = BitConverter.SingleToInt32Bits(value);
                    BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(StreamHeaderBytes + frame * 8, 4), bits);
                    BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(StreamHeaderBytes + frame * 8 + 4, 4), bits);
                }

                await socket.SendAsync(packet, WebSocketMessageType.Binary, true, CancellationToken.None);
                audioPacketCount++;

                if (packetIsTailSilence)
                    tailSilencePacketCount++;

                if (audioPacketCount <= 5 || audioPacketCount % 20 == 0)
                {
                    var phase = packetIsTailSilence ? "tail silence" : $"progress {Math.Min(100.0, sampleIndex * 100.0 / wav.MonoSamples.Length):F1}%";
                    Console.WriteLine($"TX_CHRONO #{txChronoCount}: sent WAV packet {packet.Length} bytes, {phase}");
                }

                if (sampleIndex >= wav.MonoSamples.Length && tailSilenceUntil is null)
                {
                    tailSilenceUntil = DateTime.UtcNow.AddMilliseconds(TailSilenceMilliseconds);
                    Console.WriteLine($"WAV samples complete. Keeping MOX on and feeding {TailSilenceMilliseconds} ms of TX_CHRONO-paced digital silence...");
                }

                if (tailSilenceUntil.HasValue && DateTime.UtcNow >= tailSilenceUntil.Value)
                    transmissionFinished = true;
            }
        }

        if (!transmissionFinished)
            pendingReceive = ReceiveMessageAsync();
    }

    if (!transmissionFinished)
        throw new TimeoutException("Transmit hard-stop reached before WAV/tail-silence sequence completed.");

    Console.WriteLine($"Trailing silence complete ({tailSilencePacketCount} full silent packets). UNKEYING");
    await TryUnkeyAsync();

    if (!pendingReceive.IsCompleted) { /* leave receive pending and race below */ }
    else pendingReceive = ReceiveMessageAsync();

    var observeUntil = DateTime.UtcNow.AddMilliseconds(1500);
    while (DateTime.UtcNow < observeUntil && socket.State == WebSocketState.Open && !rxConfirmed)
    {
        var completed = await Task.WhenAny(pendingReceive, Task.Delay(observeUntil - DateTime.UtcNow));
        if (completed != pendingReceive) break;
        var message = await pendingReceive;
        if (message.Type == WebSocketMessageType.Text)
        {
            var text = Encoding.UTF8.GetString(message.Payload);
            Console.WriteLine($"TEXT  {text}");
            if (text.Contains($"trx:{Transceiver},false;", StringComparison.OrdinalIgnoreCase)) rxConfirmed = true;
        }
        else if (message.Type == WebSocketMessageType.Binary)
            Console.WriteLine($"BINARY frame after unkey: {message.Payload.Length} bytes");
        pendingReceive = ReceiveMessageAsync();
    }

    Console.WriteLine($"\nTX_CHRONO frames: {txChronoCount}");
    Console.WriteLine($"WAV/Tail TX_AUDIO_STREAM packets: {audioPacketCount}");
    Console.WriteLine($"Full trailing-silence packets: {tailSilencePacketCount}");
    Console.WriteLine($"WAV samples consumed: {sampleIndex}/{wav.MonoSamples.Length}");
    if (rxConfirmed && sampleIndex >= wav.MonoSamples.Length && audioPacketCount > 0 && tailSilencePacketCount > 0)
        Console.WriteLine("*** Milestone 5 candidate success: WAV + trailing silence supplied over TCI and RX confirmed. ***");
    else
        Console.WriteLine("Milestone 5 not yet proven. Check output and confirm Thetis is in RX.");
}
catch (Exception ex)
{
    Console.WriteLine($"\nERROR: {ex.Message}");
    Environment.ExitCode = 1;
}
finally
{
    if (transmitCommandSent)
    {
        Console.WriteLine("\nSafety cleanup: attempting to force RX...");
        await TryUnkeyAsync();
    }
    if (socket.State == WebSocketState.Open)
    {
        try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "WAV diagnostic complete", CancellationToken.None); } catch { }
    }
    Console.WriteLine("Diagnostic finished. Confirm Thetis is in RX before continuing.");
}

static uint ReadU32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
static void WriteU32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);

static WavData LoadWav(string path)
{
    using var fs = File.OpenRead(path);
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
            format = br.ReadUInt16(); channels = br.ReadUInt16(); rate = br.ReadUInt32();
            br.ReadUInt32(); br.ReadUInt16(); bits = br.ReadUInt16();
        }
        else if (id == "data") data = br.ReadBytes(checked((int)size));
        fs.Position = next + (size % 2);
    }

    if (data is null) throw new InvalidDataException("No data chunk found.");
    if (channels is < 1 or > 2) throw new InvalidDataException($"Only mono/stereo WAV is supported; found {channels} channels.");
    if (format != 1 && format != 3) throw new InvalidDataException($"Only PCM (1) or IEEE float (3) WAV is supported; found format {format}.");
    if (format == 3 && bits != 32) throw new InvalidDataException("IEEE float WAV must be 32-bit.");
    if (format == 1 && bits is not (16 or 24 or 32)) throw new InvalidDataException($"PCM bit depth {bits} is not supported.");

    var bytesPerSample = bits / 8;
    var frameBytes = bytesPerSample * channels;
    var frames = data.Length / frameBytes;
    var mono = new float[frames];
    float peak = 0;
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
        var m = (float)Math.Clamp(sum / channels, -1.0, 1.0);
        mono[i] = m;
        peak = Math.Max(peak, Math.Abs(m));
    }

    return new WavData((int)rate, channels, mono, peak);
}

sealed record WavData(int SampleRate, int SourceChannels, float[] MonoSamples, float Peak)
{
    public TimeSpan Duration => TimeSpan.FromSeconds(MonoSamples.Length / (double)SampleRate);
}
