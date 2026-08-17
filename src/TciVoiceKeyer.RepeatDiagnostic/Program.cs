using TciVoiceKeyer.Engine;

Console.WriteLine("TCI Voice Keyer - repeat/listen diagnostic");
Console.WriteLine("WARNING: this test WILL transmit the selected WAV repeatedly.");
Console.WriteLine("Use USB, LSB, DIGU/DUSB or DIGL/DLSB and ensure TX is safe.\n");

if (args.Length != 4 ||
    !Uri.TryCreate(args[0], UriKind.Absolute, out var serverUri) ||
    !int.TryParse(args[2], out var repeatCount) ||
    !double.TryParse(args[3], out var listenSeconds))
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project src/TciVoiceKeyer.RepeatDiagnostic -- ws://<thetis-ip>:<port> \"C:\\path\\message.wav\" <repeat-count> <listen-seconds>");
    Console.WriteLine("Example:");
    Console.WriteLine("  dotnet run --project src/TciVoiceKeyer.RepeatDiagnostic -- ws://127.0.0.1:50001/ \"C:\\audio\\cq.wav\" 3 8");
    return;
}

var options = new KeyerOptions(
    serverUri,
    Path.GetFullPath(args[1]),
    repeatCount,
    TimeSpan.FromSeconds(listenSeconds),
    KeyerOptions.DefaultTailSilence);

try
{
    options.Validate();
}
catch (Exception ex)
{
    Console.WriteLine($"Invalid options: {ex.Message}");
    return;
}

Console.WriteLine($"WAV:          {options.WavFile}");
Console.WriteLine($"Repeat count: {options.RepeatCount}");
Console.WriteLine($"Listen time:  {options.ListenDelay.TotalSeconds:F1} s (starts after RX confirmation)");
Console.WriteLine($"TX tail:      {options.TailSilence.TotalMilliseconds:F0} ms automatic digital silence\n");
Console.WriteLine("Nothing has been transmitted yet.");
Console.WriteLine("Type exactly REPEAT and press Enter to start the full sequence.");
Console.Write("Confirmation: ");
if (!string.Equals(Console.ReadLine(), "REPEAT", StringComparison.Ordinal))
{
    Console.WriteLine("Cancelled. No PTT command was sent.");
    return;
}

await using var keyer = new VoiceKeyerEngine();
using var ctrlC = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\nCtrl+C: STOP requested; forcing RX...");
    ctrlC.Cancel();
};

keyer.StatusChanged += status =>
{
    var repeat = status.TotalRepeats > 0 ? $" [{status.CurrentRepeat}/{status.TotalRepeats}]" : "";
    var remaining = status.Remaining.HasValue ? $" {Math.Max(0, status.Remaining.Value.TotalSeconds):F1}s remaining" : "";
    Console.WriteLine($"{DateTime.Now:HH:mm:ss} {status.State}{repeat}: {status.Message}{remaining}");
};

Console.WriteLine("\nStarting. Press Ctrl+C at any time to STOP and force RX.\n");

try
{
    await keyer.StartAsync(options, ctrlC.Token);
}
catch (OperationCanceledException)
{
    // The engine reports the stop sequence itself.
}
catch (Exception ex)
{
    Console.WriteLine($"ERROR: {ex.Message}");
    Environment.ExitCode = 1;
}

Console.WriteLine("Diagnostic finished. Confirm Thetis is in RX.");
