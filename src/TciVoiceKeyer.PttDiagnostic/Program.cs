using System.Net.WebSockets;
using System.Text;

const int Transceiver = 0;
const int KeyMilliseconds = 750;

Console.WriteLine("TCI Voice Keyer - Milestone 2 PTT diagnostic");
Console.WriteLine("WARNING: this test WILL command Thetis into transmit for about 0.75 seconds.");
Console.WriteLine("It sends NO audio. Use a dummy load or otherwise ensure a brief TX is safe.\n");

if (args.Length != 1 || !Uri.TryCreate(args[0], UriKind.Absolute, out var serverUri) ||
    (serverUri.Scheme != "ws" && serverUri.Scheme != "wss"))
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project src/TciVoiceKeyer.PttDiagnostic -- ws://<thetis-ip>:<tci-port>");
    return;
}

using var socket = new ClientWebSocket();
var transmitCommandSent = false;
var txAllowed = false;
var readySeen = false;
var rxConfirmed = false;

async Task SendTextAsync(string command, CancellationToken token = default)
{
    var bytes = Encoding.UTF8.GetBytes(command);
    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token);
    Console.WriteLine($"SEND  {command}");
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
        await SendTextAsync($"trx:{Transceiver},false;", CancellationToken.None);
        transmitCommandSent = false;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"WARNING: automatic unkey command failed: {ex.Message}");
        Console.WriteLine("CHECK THETIS IMMEDIATELY AND ENSURE MOX/PTT IS OFF.");
    }
}

async Task<(WebSocketMessageType Type, byte[] Payload)> ReceiveMessageAsync(CancellationToken token)
{
    var receiveBuffer = new byte[64 * 1024];
    using var messageBuffer = new MemoryStream();
    WebSocketReceiveResult result;

    do
    {
        result = await socket.ReceiveAsync(receiveBuffer, token);
        if (result.MessageType == WebSocketMessageType.Close)
            return (WebSocketMessageType.Close, Array.Empty<byte>());

        messageBuffer.Write(receiveBuffer, 0, result.Count);
    }
    while (!result.EndOfMessage);

    return (result.MessageType, messageBuffer.ToArray());
}

void LogIncoming((WebSocketMessageType Type, byte[] Payload) message, string binaryLabel)
{
    if (message.Type == WebSocketMessageType.Text)
    {
        var text = Encoding.UTF8.GetString(message.Payload);
        Console.WriteLine($"TEXT  {text}");
        if (text.Contains($"trx:{Transceiver},false;", StringComparison.OrdinalIgnoreCase))
            rxConfirmed = true;
    }
    else if (message.Type == WebSocketMessageType.Binary)
    {
        Console.WriteLine($"{binaryLabel}: {message.Payload.Length} bytes");
    }
}

try
{
    Console.WriteLine($"Connecting to {serverUri} ...");
    await socket.ConnectAsync(serverUri, CancellationToken.None);
    Console.WriteLine("Connected. Waiting for READY and TX permission...\n");

    while (socket.State == WebSocketState.Open && !readySeen)
    {
        var message = await ReceiveMessageAsync(CancellationToken.None);
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
        Console.WriteLine("No PTT command was sent.");
        return;
    }

    Console.WriteLine("\nTCI READY received and TX is permitted on transceiver 0.");
    Console.WriteLine("Nothing has been transmitted yet.");
    Console.WriteLine("\nType exactly KEY and press Enter to perform ONE 0.75-second PTT test.");
    Console.Write("Confirmation: ");
    var confirmation = Console.ReadLine();

    if (!string.Equals(confirmation, "KEY", StringComparison.Ordinal))
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

    // IMPORTANT: do not cancel a pending ClientWebSocket.ReceiveAsync to end the TX timer.
    // In .NET, cancelling a WebSocket operation can abort the socket. If that happened while
    // keyed, the subsequent trx:false command could not be sent. Instead, the TX duration is
    // controlled by a plain delay while a receive task is allowed to remain pending.
    var pendingReceive = ReceiveMessageAsync(CancellationToken.None);
    var txDelay = Task.Delay(KeyMilliseconds);

    while (!txDelay.IsCompleted && socket.State == WebSocketState.Open)
    {
        var completed = await Task.WhenAny(pendingReceive, txDelay);
        if (completed == txDelay)
            break;

        var message = await pendingReceive;
        LogIncoming(message, "BINARY frame while keyed");
        if (message.Type == WebSocketMessageType.Close)
            break;

        pendingReceive = ReceiveMessageAsync(CancellationToken.None);
    }

    Console.WriteLine("UNKEYING");
    await TryUnkeyAsync();

    // If a receive was already pending, use it first. Do not cancel it; race it against a
    // 1.5-second observation timer so the WebSocket stays usable.
    var observeDelay = Task.Delay(1500);
    while (!observeDelay.IsCompleted && socket.State == WebSocketState.Open && !rxConfirmed)
    {
        var completed = await Task.WhenAny(pendingReceive, observeDelay);
        if (completed == observeDelay)
            break;

        var message = await pendingReceive;
        LogIncoming(message, "BINARY frame after unkey");
        if (message.Type == WebSocketMessageType.Close)
            break;

        pendingReceive = ReceiveMessageAsync(CancellationToken.None);
    }

    if (rxConfirmed)
        Console.WriteLine("\n*** Thetis confirmed RX. Milestone 2 PTT path proven. ***");
    else
    {
        Console.WriteLine("\nNo explicit RX confirmation was observed before timeout.");
        Console.WriteLine("Verify visually in Thetis that MOX/PTT is OFF.");
    }
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

    // Do not wait indefinitely for a graceful close if a receive operation remains pending.
    if (socket.State == WebSocketState.Open)
    {
        try
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "PTT diagnostic complete", CancellationToken.None);
        }
        catch
        {
            // Closing only; no additional action required.
        }
    }

    Console.WriteLine("Diagnostic finished. Confirm Thetis is in RX before continuing.");
}
