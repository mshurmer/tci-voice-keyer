using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;

const int Transceiver = 0;
const int KeyMilliseconds = 1000;
const int StreamHeaderBytes = 64;
const uint TxAudioStream = 2;
const uint TxChrono = 3;
const uint Float32 = 3;

Console.WriteLine("TCI Voice Keyer - Milestone 3 TX_CHRONO / silence diagnostic");
Console.WriteLine("WARNING: this test WILL command Thetis into transmit for about 1 second.");
Console.WriteLine("It responds to TX_CHRONO requests with DIGITAL SILENCE only.");
Console.WriteLine("Use a dummy load or otherwise ensure a brief TX is safe.\n");

if (args.Length != 1 || !Uri.TryCreate(args[0], UriKind.Absolute, out var serverUri) ||
    (serverUri.Scheme != "ws" && serverUri.Scheme != "wss"))
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project src/TciVoiceKeyer.SilenceDiagnostic -- ws://<thetis-ip>:<tci-port>");
    return;
}

using var socket = new ClientWebSocket();
var transmitCommandSent = false;
var txAllowed = false;
var readySeen = false;
var txChronoCount = 0;
var silentAudioCount = 0;

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

static int BytesPerSample(uint sampleType) => sampleType switch
{
    0 => 2,
    1 => 3,
    2 => 4,
    3 => 4,
    _ => 0
};

static byte[] BuildSilentTxAudio(byte[] chrono)
{
    if (chrono.Length < StreamHeaderBytes)
        throw new InvalidOperationException($"TX_CHRONO frame is only {chrono.Length} bytes; expected at least 64.");

    var receiver = ReadU32(chrono, 0);
    var sampleRate = ReadU32(chrono, 4);
    var sampleType = ReadU32(chrono, 8);
    var requestedLength = ReadU32(chrono, 20);
    var channels = ReadU32(chrono, 28);
    var bytesPerSample = BytesPerSample(sampleType);

    if (bytesPerSample == 0)
        throw new InvalidOperationException($"Unsupported sample type {sampleType}.");

    if (requestedLength > 16384)
        throw new InvalidOperationException($"Refusing unexpected TX_CHRONO length {requestedLength}.");

    var sampleBytes = checked((int)requestedLength * bytesPerSample);
    var packet = new byte[StreamHeaderBytes + sampleBytes]; // Array is zero-filled = digital silence.

    WriteU32(packet, 0, receiver);
    WriteU32(packet, 4, sampleRate);
    WriteU32(packet, 8, sampleType);
    WriteU32(packet, 12, 0); // codec
    WriteU32(packet, 16, 0); // crc
    WriteU32(packet, 20, requestedLength);
    WriteU32(packet, 24, TxAudioStream);
    WriteU32(packet, 28, channels);
    // reserved uint32[8] remains zero.

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

        if (text.Contains("ready;", StringComparison.OrdinalIgnoreCase))
            readySeen = true;
    }

    if (!txAllowed)
    {
        Console.WriteLine($"\nABORTED: Thetis did not report tx_enable:{Transceiver},true;.");
        return;
    }

    Console.WriteLine("\nConfiguring an explicit, conservative TCI TX audio format:");
    await SendTextAsync("audio_samplerate:48000;");
    await SendTextAsync("audio_stream_sample_type:float32;");
    await SendTextAsync("audio_stream_channels:2;");
    await SendTextAsync("audio_stream_samples:512;");
    await SendTextAsync("tx_stream_audio_buffering:100;");

    Console.WriteLine("\nNothing has been transmitted yet.");
    Console.WriteLine("Type exactly SILENCE and press Enter to perform ONE 1-second silent-TX test.");
    Console.Write("Confirmation: ");
    var confirmation = Console.ReadLine();

    if (!string.Equals(confirmation, "SILENCE", StringComparison.Ordinal))
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

    Console.WriteLine("\nKEYING NOW");
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
        else if (message.Type == WebSocketMessageType.Binary)
        {
            if (message.Payload.Length < StreamHeaderBytes)
            {
                Console.WriteLine($"BINARY unexpected short frame: {message.Payload.Length} bytes");
            }
            else
            {
                var receiver = ReadU32(message.Payload, 0);
                var sampleRate = ReadU32(message.Payload, 4);
                var sampleType = ReadU32(message.Payload, 8);
                var length = ReadU32(message.Payload, 20);
                var streamType = ReadU32(message.Payload, 24);
                var channels = ReadU32(message.Payload, 28);

                Console.WriteLine($"BINARY type={streamType} receiver={receiver} rate={sampleRate} sample={SampleTypeName(sampleType)} length={length} channels={channels} bytes={message.Payload.Length}");

                if (streamType == TxChrono)
                {
                    txChronoCount++;
                    var silence = BuildSilentTxAudio(message.Payload);
                    await SendBinaryAsync(silence);
                    silentAudioCount++;
                    Console.WriteLine($"  -> TX_CHRONO #{txChronoCount}: sent silent TX_AUDIO_STREAM ({silence.Length} bytes)");
                }
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
    Console.WriteLine($"Silent TX_AUDIO_STREAM frames sent: {silentAudioCount}");

    if (rxConfirmed && txChronoCount > 0 && silentAudioCount == txChronoCount)
        Console.WriteLine("*** Milestone 3 candidate success: TX_CHRONO decoded, silence supplied, and RX confirmed. ***");
    else
        Console.WriteLine("Milestone 3 not yet proven. Check the output above and visually confirm Thetis is in RX.");
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
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Silence diagnostic complete", CancellationToken.None);
        }
        catch
        {
            // Closing only.
        }
    }

    Console.WriteLine("Diagnostic finished. Confirm Thetis is in RX before continuing.");
}
