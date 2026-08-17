using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;

const int Transceiver = 0;
const int KeyMilliseconds = 750;
const int StreamHeaderBytes = 64;
const uint TxAudioStream = 2;
const uint TxChrono = 3;
const uint Float32 = 3;
const int ExpectedSampleRate = 48000;
const int ExpectedChannels = 2;
const double ToneHz = 1000.0;
const double ToneAmplitude = 0.10; // -20 dBFS peak

Console.WriteLine("TCI Voice Keyer - Milestone 4 generated-tone diagnostic");
Console.WriteLine("WARNING: this test WILL command Thetis into transmit for about 0.75 seconds.");
Console.WriteLine("It sends a generated 1 kHz tone at -20 dBFS via TCI TX audio.");
Console.WriteLine("For this test, put Thetis in USB mode and use a dummy load or otherwise ensure TX is safe.\n");

if (args.Length != 1 || !Uri.TryCreate(args[0], UriKind.Absolute, out var serverUri) ||
    (serverUri.Scheme != "ws" && serverUri.Scheme != "wss"))
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project src/TciVoiceKeyer.ToneDiagnostic -- ws://<thetis-ip>:<tci-port>");
    return;
}

using var socket = new ClientWebSocket();
var transmitCommandSent = false;
var txAllowed = false;
var readySeen = false;
var modulation = "unknown";
var txChronoCount = 0;
var toneAudioCount = 0;
long stereoFrameCursor = 0;

async Task SendTextAsync(string command)
{
    var bytes = Encoding.UTF8.GetBytes(command);
    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    Console.WriteLine($"SEND  {command}");
}

async Task SendBinaryAsync(byte[] payload)
{
    await socket.SendAsync(payload, WebSocketMessageType.Binary, true, CancellationToken.None);
}

async Task TryUnkeyAsync()
{
    if (socket.State != WebSocketState.Open)
    {
        Console.WriteLine($"WARNING: cannot send automatic unkey because WebSocket state is {socket.State}.");
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
        Console.WriteLine($"WARNING: automatic unkey command failed: {ex.Message}");
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

static uint ReadU32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
static void WriteU32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);

static string SampleTypeName(uint sampleType) => sampleType switch
{
    0 => "int16",
    1 => "int24",
    2 => "int32",
    3 => "float32",
    _ => $"unknown({sampleType})"
};

byte[] BuildToneTxAudio(byte[] chrono)
{
    if (chrono.Length < StreamHeaderBytes)
        throw new InvalidOperationException($"TX_CHRONO frame is only {chrono.Length} bytes; expected at least 64.");

    var receiver = ReadU32(chrono, 0);
    var sampleRate = ReadU32(chrono, 4);
    var sampleType = ReadU32(chrono, 8);
    var requestedLength = ReadU32(chrono, 20);
    var channels = ReadU32(chrono, 28);

    if (sampleType != Float32)
        throw new InvalidOperationException($"Refusing tone TX because Thetis requested {SampleTypeName(sampleType)}, not float32.");
    if (sampleRate != ExpectedSampleRate)
        throw new InvalidOperationException($"Refusing tone TX because Thetis requested sample rate {sampleRate}, not {ExpectedSampleRate}.");
    if (channels != ExpectedChannels)
        throw new InvalidOperationException($"Refusing tone TX because Thetis requested {channels} channels, not {ExpectedChannels}.");
    if (requestedLength == 0 || requestedLength > 16384 || requestedLength % channels != 0)
        throw new InvalidOperationException($"Refusing unexpected TX_CHRONO length {requestedLength} for {channels} channels.");

    var sampleBytes = checked((int)requestedLength * sizeof(float));
    var packet = new byte[StreamHeaderBytes + sampleBytes];

    WriteU32(packet, 0, receiver);
    WriteU32(packet, 4, sampleRate);
    WriteU32(packet, 8, sampleType);
    WriteU32(packet, 12, 0); // codec
    WriteU32(packet, 16, 0); // crc
    WriteU32(packet, 20, requestedLength);
    WriteU32(packet, 24, TxAudioStream);
    WriteU32(packet, 28, channels);

    var frames = requestedLength / channels;
    for (var frame = 0u; frame < frames; frame++)
    {
        var phase = 2.0 * Math.PI * ToneHz * stereoFrameCursor / sampleRate;
        var value = (float)(ToneAmplitude * Math.Sin(phase));
        stereoFrameCursor++;

        for (var channel = 0u; channel < channels; channel++)
        {
            var scalarIndex = frame * channels + channel;
            var bits = BitConverter.SingleToUInt32Bits(value);
            WriteU32(packet, StreamHeaderBytes + checked((int)scalarIndex * sizeof(float)), bits);
        }
    }

    return packet;
}

try
{
    Console.WriteLine($"Connecting to {serverUri} ...");
    await socket.ConnectAsync(serverUri, CancellationToken.None);
    Console.WriteLine("Connected. Waiting for READY and TX permission...\n");

    while (socket.State == WebSocketState.Open && !readySeen)
    {
        var message = await ReceiveMessageAsync();
        if (message.Type == WebSocketMessageType.Close)
            throw new InvalidOperationException("Thetis closed the WebSocket during initialization.");

        if (message.Type != WebSocketMessageType.Text)
            continue;

        var text = Encoding.UTF8.GetString(message.Payload);
        Console.WriteLine($"TEXT  {text}");

        if (text.Contains($"tx_enable:{Transceiver},true;", StringComparison.OrdinalIgnoreCase))
            txAllowed = true;

        var modulationPrefix = $"modulation:{Transceiver},";
        if (text.StartsWith(modulationPrefix, StringComparison.OrdinalIgnoreCase))
            modulation = text[modulationPrefix.Length..].TrimEnd(';').Trim();

        if (text.Contains("ready;", StringComparison.OrdinalIgnoreCase))
            readySeen = true;
    }

    if (!txAllowed)
    {
        Console.WriteLine($"\nABORTED: Thetis did not report tx_enable:{Transceiver},true;.");
        return;
    }

    if (!string.Equals(modulation, "USB", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"\nABORTED: Thetis is currently in {modulation} mode, not USB.");
        Console.WriteLine("Set VFO A / receiver 0 to USB in Thetis, then run this diagnostic again.");
        Console.WriteLine("No PTT command was sent.");
        return;
    }

    Console.WriteLine("\nConfiguring the proven Milestone 3 TCI TX audio format:");
    await SendTextAsync("audio_samplerate:48000;");
    await SendTextAsync("audio_stream_sample_type:float32;");
    await SendTextAsync("audio_stream_channels:2;");
    await SendTextAsync("audio_stream_samples:512;");
    await SendTextAsync("tx_stream_audio_buffering:100;");

    Console.WriteLine("\nNothing has been transmitted yet.");
    Console.WriteLine("Thetis reports USB mode and TX is permitted.");
    Console.WriteLine("Type exactly TONE and press Enter to perform ONE 0.75-second 1 kHz tone test.");
    Console.Write("Confirmation: ");
    var confirmation = Console.ReadLine();

    if (!string.Equals(confirmation, "TONE", StringComparison.Ordinal))
    {
        Console.WriteLine("Cancelled. No PTT command was sent.");
        return;
    }

    Console.WriteLine("\nFinal countdown:");
    for (var i = 3; i >= 1; i--)
    {
        Console.WriteLine($"  {i}...");
        await Task.Delay(1000);
    }

    Console.WriteLine("\nKEYING NOW - generated 1 kHz tone at -20 dBFS");
    await SendTextAsync($"trx:{Transceiver},true,tci;");
    transmitCommandSent = true;

    var txEndsAt = DateTime.UtcNow.AddMilliseconds(KeyMilliseconds);
    var pendingReceive = ReceiveMessageAsync();

    while (DateTime.UtcNow < txEndsAt && socket.State == WebSocketState.Open)
    {
        var remaining = txEndsAt - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
            break;

        var completed = await Task.WhenAny(pendingReceive, Task.Delay(remaining));
        if (completed != pendingReceive)
            break;

        var message = await pendingReceive;

        if (message.Type == WebSocketMessageType.Text)
        {
            Console.WriteLine($"TEXT  {Encoding.UTF8.GetString(message.Payload)}");
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
                var tonePacket = BuildToneTxAudio(message.Payload);
                await SendBinaryAsync(tonePacket);
                toneAudioCount++;

                if (txChronoCount <= 5 || txChronoCount % 20 == 0)
                    Console.WriteLine($"TX_CHRONO #{txChronoCount}: rate={sampleRate} {SampleTypeName(sampleType)} length={length} channels={channels} -> tone packet {tonePacket.Length} bytes");
            }
            else
            {
                Console.WriteLine($"BINARY type={streamType} receiver={receiver} rate={sampleRate} length={length} channels={channels} bytes={message.Payload.Length}");
            }
        }
        else if (message.Type == WebSocketMessageType.Close)
        {
            Console.WriteLine("Thetis closed the connection while keyed.");
            break;
        }

        pendingReceive = ReceiveMessageAsync();
    }

    Console.WriteLine("UNKEYING");
    await TryUnkeyAsync();

    var observeEndsAt = DateTime.UtcNow.AddMilliseconds(1500);
    var rxConfirmed = false;
    while (DateTime.UtcNow < observeEndsAt && socket.State == WebSocketState.Open && !rxConfirmed)
    {
        var remaining = observeEndsAt - DateTime.UtcNow;
        var completed = await Task.WhenAny(pendingReceive, Task.Delay(remaining));
        if (completed != pendingReceive)
            break;

        var message = await pendingReceive;
        if (message.Type == WebSocketMessageType.Text)
        {
            var text = Encoding.UTF8.GetString(message.Payload);
            Console.WriteLine($"TEXT  {text}");
            if (text.Contains($"trx:{Transceiver},false;", StringComparison.OrdinalIgnoreCase))
                rxConfirmed = true;
        }
        else if (message.Type == WebSocketMessageType.Binary)
        {
            Console.WriteLine($"BINARY frame after unkey: {message.Payload.Length} bytes");
        }

        pendingReceive = ReceiveMessageAsync();
    }

    Console.WriteLine($"\nTX_CHRONO frames decoded: {txChronoCount}");
    Console.WriteLine($"Tone TX_AUDIO_STREAM frames sent: {toneAudioCount}");

    if (rxConfirmed && txChronoCount > 0 && toneAudioCount == txChronoCount)
        Console.WriteLine("*** Milestone 4 candidate success: generated tone samples supplied and RX confirmed. ***");
    else
        Console.WriteLine("Milestone 4 not yet proven. Check the output above and visually confirm Thetis is in RX.");
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
        try
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Tone diagnostic complete", CancellationToken.None);
        }
        catch
        {
            // Closing only.
        }
    }

    Console.WriteLine("Diagnostic finished. Confirm Thetis is in RX before continuing.");
}
