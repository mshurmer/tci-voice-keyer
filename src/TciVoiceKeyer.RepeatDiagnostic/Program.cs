using TciVoiceKeyer.Engine;

Console.WriteLine("TCI Voice Keyer - repeat/listen/volume diagnostic");
Console.WriteLine("WARNING: this test WILL transmit the selected WAV repeatedly.");
Console.WriteLine("Use USB, LSB, DIGU/DUSB or DIGL/DLSB and ensure TX is safe.\n");

if ((args.Length != 4 && args.Length != 5) ||
    !Uri.TryCreate(args[0], UriKind.Absolute, out var serverUri) ||
    !int.TryParse(args[2], out var repeatCount) ||
    !double.TryParse(args[3], out var listenSeconds) ||
    (args.Length == 5 && !double.TryParse(args[4], out _)))
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project src/TciVoiceKeyer.RepeatDiagnostic -- ws://<thetis-ip>:<port> \"C:\\path\\message.wav\" <repeat-count> <listen-seconds> [volume-percent]");
    Console.WriteLine("Examples:");
    Console.WriteLine("  ... \"C:\\audio\\cq.wav\" 3 8");
    Console.WriteLine("  ... \"C:\\audio\\cq.wav\" 3 8 50");
    return;
}

var volumePercent = args.Length == 5 ? double.Parse(args[4]) : 100.0;

var options = new KeyerOptions(
    serverUri,
    Path.GetFullPath(args[1]),
    repeatCount,
    TimeSpan.FromSeconds(listenSeconds),
    KeyerOptions.DefaultTailSilence,
    volumePercent);

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
Console.WriteLine($"WAV volume:   {options.VolumePercent:F0}%");
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

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\nCtrl+C: STOP requested; sending force-RX before cancelling the run...");
    _ = keyer.StopAsync();
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
    await keyer.StartAsync(options);
}
catch (Exception ex)
{
    Console.WriteLine($"ERROR: {ex.Message}");
    Environment.ExitCode = 1;
}

Console.WriteLine("Diagnostic finished. Confirm Thetis is in RX.");
