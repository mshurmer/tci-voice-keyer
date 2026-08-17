using System.Net.WebSockets;
using System.Text;

var pttTest = args.Any(a => a.Equals("--ptt-test", StringComparison.OrdinalIgnoreCase));
var uriArg = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));

Console.WriteLine("TCI Voice Keyer diagnostic");
Console.WriteLine(pttTest
    ? "WARNING: PTT TEST ENABLED. This mode can key the transmitter. No TX audio is sent."
    : "Read-only mode. It does not key the transmitter or send audio.");
Console.WriteLine();

if (uriArg is null || !Uri.TryCreate(uriArg, UriKind.Absolute, out var serverUri) ||
    (serverUri.Scheme != "ws" && serverUri.Scheme != "wss"))
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  Read only:");
    Console.WriteLine("    dotnet run --project src/TciVoiceKeyer.Console -- ws://<thetis-ip>:<tci-port>/");
    Console.WriteLine();
    Console.WriteLine("  Milestone 2 PTT test:");
    Console.WriteLine("    dotnet run --project src/TciVoiceKeyer.Console -- ws://<thetis-ip>:<tci-port>/ --ptt-test");
    return;
}

using var socket = new ClientWebSocket();
using var shutdown = new CancellationTokenSource();
var pttWasRequested = false;

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

async Task SendTextAsync(string command, CancellationToken token = default)
{
    if (socket.State != WebSocketState.Open)
        return;

    var bytes = Encoding.UTF8.GetBytes(command);
    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token);
    Console.WriteLine($"SEND  {command}");
}

try
{
    Console.WriteLine($"Connecting to {serverUri} ...");
    await socket.ConnectAsync(serverUri, shutdown.Token);
    Console.WriteLine("Connected. Waiting for TCI initialization messages...");
    Console.WriteLine("Press Ctrl+C to stop.\n");

    var receiveBuffer = new byte[64 * 1024];
    var messageBuffer = new MemoryStream();
    var readySeen = false;
    var txEnabledTrx0 = false;
    var pttTestStarted = false;

    while (socket.State == WebSocketState.Open && !shutdown.IsCancellationRequested)
    {
        messageBuffer.SetLength(0);
        WebSocketReceiveResult result;

        do
        {
            result = await socket.ReceiveAsync(receiveBuffer, shutdown.Token);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                Console.WriteLine($"Server requested close: {result.CloseStatus} {result.CloseStatusDescription}");
                if (socket.State == WebSocketState.CloseReceived)
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Client closing", CancellationToken.None);
                return;
            }

            messageBuffer.Write(receiveBuffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        var payload = messageBuffer.ToArray();

        if (result.MessageType == WebSocketMessageType.Text)
        {
            var text = Encoding.UTF8.GetString(payload);
            Console.WriteLine($"TEXT  {text}");

            if (text.Contains("tx_enable:0,true;", StringComparison.OrdinalIgnoreCase))
                txEnabledTrx0 = true;

            if (!readySeen && text.Contains("ready;", StringComparison.OrdinalIgnoreCase))
            {
                readySeen = true;
                Console.WriteLine("\n*** TCI READY received - connection proven. ***\n");
            }

            if (pttTest && readySeen && !pttTestStarted)
            {
                pttTestStarted = true;

                if (!txEnabledTrx0)
                {
                    Console.WriteLine("PTT TEST ABORTED: Thetis did not report tx_enable:0,true;");
                    break;
                }

                Console.WriteLine("PTT test will start in 3 seconds.");
                Console.WriteLine("Ensure transmitting for approximately 1 second is safe on the current frequency/load.");
                await Task.Delay(TimeSpan.FromSeconds(3), shutdown.Token);

                Console.WriteLine("Requesting TX on transceiver 0 using TCI audio source...");
                pttWasRequested = true;
                await SendTextAsync("trx:0,true,tci;", shutdown.Token);

                await Task.Delay(TimeSpan.FromSeconds(1), shutdown.Token);

                Console.WriteLine("Requesting RX...");
                await SendTextAsync("trx:0,false;", CancellationToken.None);
                pttWasRequested = false;

                Console.WriteLine("\n*** Milestone 2 PTT command sequence completed. ***");
                Console.WriteLine("Waiting briefly for final Thetis state messages...\n");
                await Task.Delay(TimeSpan.FromSeconds(2), shutdown.Token);
                break;
            }
        }
        else if (result.MessageType == WebSocketMessageType.Binary)
        {
            Console.WriteLine($"BINARY frame: {payload.Length} bytes");
        }
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("\nStopping diagnostic...");
}
catch (WebSocketException ex)
{
    Console.WriteLine($"WebSocket error: {ex.Message}");
    Environment.ExitCode = 1;
}
catch (Exception ex)
{
    Console.WriteLine($"Unexpected error: {ex.Message}");
    Environment.ExitCode = 1;
}
finally
{
    if (pttWasRequested && socket.State == WebSocketState.Open)
    {
        try
        {
            Console.WriteLine("FAIL-SAFE: requesting RX before exit...");
            await SendTextAsync("trx:0,false;", CancellationToken.None);
        }
        catch
        {
            Console.WriteLine("WARNING: fail-safe RX command could not be sent. Verify Thetis is in RX manually.");
        }
    }

    if (socket.State == WebSocketState.Open)
    {
        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Diagnostic complete", CancellationToken.None);
        }
        catch
        {
            // We are shutting down; no further action is required.
        }
    }
}
