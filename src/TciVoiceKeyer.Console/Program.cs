using System.Net.WebSockets;
using System.Text;

Console.WriteLine("TCI Voice Keyer - Milestone 1 connection diagnostic");
Console.WriteLine("This version is READ ONLY. It does not key the transmitter or send audio.\n");

if (args.Length != 1 || !Uri.TryCreate(args[0], UriKind.Absolute, out var serverUri) ||
    (serverUri.Scheme != "ws" && serverUri.Scheme != "wss"))
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project src/TciVoiceKeyer.Console -- ws://<thetis-ip>:<tci-port>");
    return;
}

using var socket = new ClientWebSocket();
using var shutdown = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

try
{
    Console.WriteLine($"Connecting to {serverUri} ...");
    await socket.ConnectAsync(serverUri, shutdown.Token);
    Console.WriteLine("Connected. Waiting for TCI initialization messages...");
    Console.WriteLine("Press Ctrl+C to stop.\n");

    var receiveBuffer = new byte[64 * 1024];
    var messageBuffer = new MemoryStream();
    var readySeen = false;

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
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Client closing", CancellationToken.None);
                }
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

            if (!readySeen && text.Contains("READY;", StringComparison.OrdinalIgnoreCase))
            {
                readySeen = true;
                Console.WriteLine("\n*** TCI READY received - Milestone 1 connection proven. ***\n");
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
