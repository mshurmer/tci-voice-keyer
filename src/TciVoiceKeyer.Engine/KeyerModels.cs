namespace TciVoiceKeyer.Engine;

public sealed record KeyerOptions(
    Uri ServerUri,
    string WavFile,
    int RepeatCount,
    TimeSpan ListenDelay,
    TimeSpan TailSilence)
{
    public static readonly TimeSpan DefaultTailSilence = TimeSpan.FromMilliseconds(500);

    public void Validate()
    {
        if (ServerUri.Scheme is not ("ws" or "wss"))
            throw new ArgumentException("Server URI must use ws:// or wss://.", nameof(ServerUri));
        if (string.IsNullOrWhiteSpace(WavFile))
            throw new ArgumentException("A WAV file is required.", nameof(WavFile));
        if (RepeatCount is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(RepeatCount), "Repeat count must be between 1 and 100.");
        if (ListenDelay < TimeSpan.Zero || ListenDelay > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(ListenDelay), "Listen delay must be between 0 and 10 minutes.");
        if (TailSilence < TimeSpan.Zero || TailSilence > TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(nameof(TailSilence), "Tail silence must be between 0 and 5 seconds.");
    }
}

public enum KeyerState
{
    Idle,
    Connecting,
    Ready,
    Transmitting,
    TailSilence,
    WaitingForRx,
    Listening,
    Completed,
    Stopping,
    Error
}

public sealed record KeyerStatus(
    KeyerState State,
    string Message,
    int CurrentRepeat = 0,
    int TotalRepeats = 0,
    TimeSpan? Remaining = null);
