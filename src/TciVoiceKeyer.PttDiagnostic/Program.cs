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

async Task SendTextAsync(string command, CancellationToken token = default)
{
    var bytes = Encoding.UTF8.GetBytes(command);
    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token);
    Console.WriteLine($"SEND  {command}");
}

async Task TryUnkeyAsync()
{
    if (socket.State != WebSocketState.Open)
        return;

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

    using (var txWindow = new CancellationTokenSource(KeyMilliseconds))
    {
        try
        {
            while (!txWindow.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var message = await ReceiveMessageAsync(txWindow.Token);

                if (message.Type == WebSocketMessageType.Text)
                {
                    var text = Encoding.UTF8.GetString(message.Payload);
                    Console.WriteLine($"TEXT  {text}");
                }
                else if (message.Type == WebSocketMessageType.Binary)
                {
                    Console.WriteLine($"BINARY frame while keyed: {message.Payload.Length} bytes");
                }
                else if (message.Type == WebSocketMessageType.Close)
                {
                    Console.WriteLine("Thetis closed the connection while keyed.");
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: the short transmit window expired.
        }
    }

    Console.WriteLine("UNKEYING");
    await TryUnkeyAsync();

    using var observeRx = new CancellationTokenSource(1500);
    try
    {
        while (!observeRx.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var message = await ReceiveMessageAsync(observeRx.Token);
            if (message.Type == WebSocketMessageType.Text)
            {
                var text = Encoding.UTF8.GetString(message.Payload);
                Console.WriteLine($"TEXT  {text}");
                if (text.Contains($"trx:{Transceiver},false;", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("\n*** Thetis confirmed RX. Milestone 2 PTT path proven. ***");
                    break;
                }
            }
            else if (message.Type == WebSocketMessageType.Binary)
            {
                Console.WriteLine($"BINARY frame after unkey: {message.Payload.Length} bytes");
            }
        }
    }
    catch (OperationCanceledException)
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

    if (socket.State == WebSocketState.Open)
    {
        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "PTT diagnostic complete", CancellationToken.None);
        }
        catch
        {
            // Closing only; no additional action required.
        }
    }

    Console.WriteLine("Diagnostic finished. Confirm Thetis is in RX before continuing.");
}
