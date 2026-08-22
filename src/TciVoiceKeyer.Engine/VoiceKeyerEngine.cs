using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;

namespace TciVoiceKeyer.Engine;

public sealed class VoiceKeyerEngine : IAsyncDisposable
{
    private const int Transceiver = 0;
    private const int StreamHeaderBytes = 64;
    private const uint TxAudioStream = 2;
    private const uint TxChrono = 3;
    private const int RequiredRate = 48000;
    private const int FadeOutMilliseconds = 20;
    private const int MaxTransmitSeconds = 30;

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _runCts;
    private bool _txCommandSent;
    private Task<(WebSocketMessageType Type, byte[] Payload)>? _pendingReceive;

    public event Action<KeyerStatus>? StatusChanged;

    public bool IsRunning => _runCts is not null;

    public async Task StartAsync(KeyerOptions options, CancellationToken cancellationToken = default)
    {
        if (IsRunning)
            throw new InvalidOperationException("The keyer is already running.");

        options.Validate();
        var wav = LoadWav(Path.GetFullPath(options.WavFile));
        if (wav.SampleRate != RequiredRate)
            throw new InvalidDataException($"WAV sample rate is {wav.SampleRate} Hz; exactly {RequiredRate} Hz is required.");
        if (wav.Duration.TotalSeconds > MaxTransmitSeconds)
            throw new InvalidDataException($"WAV duration is {wav.Duration.TotalSeconds:F1}s; safety limit is {MaxTransmitSeconds}s.");

        var clippedFrames = ApplyVolume(wav.MonoSamples, options.VolumePercent);
        ApplyFadeOut(wav.MonoSamples, wav.SampleRate, FadeOutMilliseconds);

        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _runCts.Token;
        _socket = new ClientWebSocket();
        _txCommandSent = false;
        _pendingReceive = null;

        try
        {
            Report(KeyerState.Connecting, $"Connecting to {options.ServerUri}...", 0, options.RepeatCount);
            await _socket.ConnectAsync(options.ServerUri, ct);

            var init = await WaitForReadyAsync(ct);
            if (!init.TxAllowed)
                throw new InvalidOperationException("Thetis did not report tx_enable:0,true;.");
            if (!IsSupportedVoiceMode(init.Mode))
                throw new InvalidOperationException($"Unsupported mode '{init.Mode ?? "unknown"}'. Use USB, LSB, DIGU/DUSB or DIGL/DLSB.");

            await SendTextAsync("audio_samplerate:48000;", ct);
            await SendTextAsync("audio_stream_sample_type:float32;", ct);
            await SendTextAsync("audio_stream_channels:2;", ct);
            await SendTextAsync("audio_stream_samples:512;", ct);
            await SendTextAsync("tx_stream_audio_buffering:100;", ct);

            var clippingText = clippedFrames > 0 ? $", WARNING {clippedFrames} clipped frame(s)" : string.Empty;
            Report(KeyerState.Ready,
                $"Ready in {init.Mode}. WAV {wav.Duration.TotalSeconds:F2}s, volume {options.VolumePercent:F0}%, repeat {options.RepeatCount}{clippingText}.",
                0, options.RepeatCount);

            for (var repeat = 1; repeat <= options.RepeatCount; repeat++)
            {
                ct.ThrowIfCancellationRequested();
                await TransmitOnceAsync(wav, options, repeat, ct);

                if (repeat < options.RepeatCount && options.ListenDelay > TimeSpan.Zero)
                    await ListenDelayAsync(options.ListenDelay, repeat, options.RepeatCount, ct);
            }

            Report(KeyerState.Completed, $"Completed {options.RepeatCount} transmission(s).", options.RepeatCount, options.RepeatCount);
        }
        catch (OperationCanceledException) when (_runCts?.IsCancellationRequested == true)
        {
            Report(KeyerState.Stopping, "Stopped by user. Forcing RX.");
            await TryUnkeyAsync(CancellationToken.None);
            Report(KeyerState.Idle, "Stopped and returned to RX.");
        }
        catch (Exception ex)
        {
            Report(KeyerState.Error, ex.Message);
            await TryUnkeyAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            if (_txCommandSent)
                await TryUnkeyAsync(CancellationToken.None);

            if (_socket?.State == WebSocketState.Open)
            {
                try { await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Keyer run complete", CancellationToken.None); }
                catch { }
            }

            _socket?.Dispose();
            _socket = null;
            _pendingReceive = null;
            _runCts?.Dispose();
            _runCts = null;
            _txCommandSent = false;
        }
    }

    public async Task StopAsync()
    {
        var cts = _runCts;
        if (cts is null)
            return;

        Report(KeyerState.Stopping, "Stop requested. Forcing RX...");
        // Safety order matters: send the unkey command BEFORE cancelling the run.
        // Cancelling a pending ClientWebSocket ReceiveAsync can poison/abort the socket.
        await TryUnkeyAsync(CancellationToken.None);
        cts.Cancel();
    }

    private async Task TransmitOnceAsync(WavData wav, KeyerOptions options, int repeat, CancellationToken ct)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
            throw new InvalidOperationException("TCI socket is not open.");

        var sampleIndex = 0;
        var tailStarted = false;
        DateTime? tailUntil = null;
        var rxConfirmed = false;
        var hardStop = DateTime.UtcNow.AddSeconds(MaxTransmitSeconds + Math.Max(3, options.TailSilence.TotalSeconds + 2));

        Report(KeyerState.Transmitting,
            $"Transmission {repeat} of {options.RepeatCount} at {options.VolumePercent:F0}% WAV volume",
            repeat, options.RepeatCount);
        await SendTextAsync($"trx:{Transceiver},true,tci;", ct);
        _txCommandSent = true;
        _pendingReceive ??= ReceiveMessageAsync(ct);

        while (DateTime.UtcNow < hardStop && _socket.State == WebSocketState.Open)
        {
            ct.ThrowIfCancellationRequested();
            var message = await _pendingReceive;
            _pendingReceive = null;

            if (message.Type == WebSocketMessageType.Close)
                throw new InvalidOperationException("Thetis closed the connection during TX.");

            if (message.Type == WebSocketMessageType.Binary && message.Payload.Length >= StreamHeaderBytes)
            {
                var streamType = ReadU32(message.Payload, 24);
                if (streamType == TxChrono)
                {
                    ValidateChrono(message.Payload);
                    var packet = BuildAudioPacket(message.Payload, wav.MonoSamples, ref sampleIndex);
                    await _socket.SendAsync(packet, WebSocketMessageType.Binary, true, ct);

                    if (sampleIndex >= wav.MonoSamples.Length && !tailStarted)
                    {
                        tailStarted = true;
                        tailUntil = DateTime.UtcNow + options.TailSilence;
                        Report(KeyerState.TailSilence,
                            $"Transmission {repeat}: feeding {options.TailSilence.TotalMilliseconds:F0} ms tail silence.",
                            repeat, options.RepeatCount);
                    }

                    if (tailStarted && tailUntil.HasValue && DateTime.UtcNow >= tailUntil.Value)
                        break;
                }
            }

            _pendingReceive = ReceiveMessageAsync(ct);
        }

        if (!tailStarted)
            throw new TimeoutException("Transmit hard-stop reached before WAV playback completed.");

        Report(KeyerState.WaitingForRx,
            $"Transmission {repeat}: unkeying and waiting for RX confirmation.",
            repeat, options.RepeatCount);
        await SendTextAsync($"trx:{Transceiver},false;", ct);
        _txCommandSent = false;

        _pendingReceive ??= ReceiveMessageAsync(ct);
        var rxDeadline = DateTime.UtcNow.AddSeconds(2);
        while (!rxConfirmed && DateTime.UtcNow < rxDeadline && _socket.State == WebSocketState.Open)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = rxDeadline - DateTime.UtcNow;
            var completed = await Task.WhenAny(_pendingReceive, Task.Delay(remaining, ct));
            if (completed != _pendingReceive)
                break;

            var message = await _pendingReceive;
            _pendingReceive = null;
            if (message.Type == WebSocketMessageType.Text)
            {
                var text = Encoding.UTF8.GetString(message.Payload);
                if (text.Contains($"trx:{Transceiver},false;", StringComparison.OrdinalIgnoreCase))
                    rxConfirmed = true;
            }
            _pendingReceive = ReceiveMessageAsync(ct);
        }

        if (!rxConfirmed)
            throw new TimeoutException("Thetis did not confirm RX after unkey.");
    }

    private async Task ListenDelayAsync(TimeSpan delay, int completedRepeat, int totalRepeats, CancellationToken ct)
    {
        var end = DateTime.UtcNow + delay;
        while (DateTime.UtcNow < end)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = end - DateTime.UtcNow;
            Report(KeyerState.Listening,
                $"Listening after transmission {completedRepeat}; next is {completedRepeat + 1} of {totalRepeats}.",
                completedRepeat, totalRepeats, remaining);
            await Task.Delay(remaining > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : remaining, ct);
        }
    }

    private async Task<(bool TxAllowed, string? Mode)> WaitForReadyAsync(CancellationToken ct)
    {
        var txAllowed = false;
        string? mode = null;

        while (_socket?.State == WebSocketState.Open)
        {
            ct.ThrowIfCancellationRequested();
            var message = await ReceiveMessageAsync(ct);
            if (message.Type == WebSocketMessageType.Close)
                throw new InvalidOperationException("Thetis closed the WebSocket during initialization.");
            if (message.Type != WebSocketMessageType.Text)
                continue;

            var text = Encoding.UTF8.GetString(message.Payload);
            if (text.Contains($"tx_enable:{Transceiver},true;", StringComparison.OrdinalIgnoreCase))
                txAllowed = true;

            foreach (var candidate in new[] { "USB", "LSB", "DIGU", "DIGL", "DUSB", "DLSB" })
            {
                if (text.Contains($"modulation:{Transceiver},{candidate};", StringComparison.OrdinalIgnoreCase))
                    mode = candidate;
            }

            if (text.Contains("ready;", StringComparison.OrdinalIgnoreCase))
                return (txAllowed, mode);
        }

        throw new InvalidOperationException("TCI socket closed before READY.");
    }

    private async Task SendTextAsync(string command, CancellationToken ct)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
            throw new InvalidOperationException("TCI socket is not open.");
        await _socket.SendAsync(Encoding.UTF8.GetBytes(command), WebSocketMessageType.Text, true, ct);
    }

    private async Task TryUnkeyAsync(CancellationToken ct)
    {
        if (_socket?.State != WebSocketState.Open)
            return;
        try
        {
            await _socket.SendAsync(Encoding.UTF8.GetBytes($"trx:{Transceiver},false;"), WebSocketMessageType.Text, true, ct);
            _txCommandSent = false;
        }
        catch { }
    }

    private async Task<(WebSocketMessageType Type, byte[] Payload)> ReceiveMessageAsync(CancellationToken ct)
    {
        if (_socket is null)
            throw new InvalidOperationException("TCI socket is not available.");

        var receiveBuffer = new byte[64 * 1024];
        using var messageBuffer = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            // Deliberately never cancel an in-flight ReceiveAsync. Earlier live testing
            // proved that cancelling it can abort the ClientWebSocket and prevent a safe
            // unkey. Cancellation is checked between complete WebSocket messages instead.
            result = await _socket.ReceiveAsync(receiveBuffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
                return (WebSocketMessageType.Close, Array.Empty<byte>());
            messageBuffer.Write(receiveBuffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return (result.MessageType, messageBuffer.ToArray());
    }

    private void Report(KeyerState state, string message, int currentRepeat = 0, int totalRepeats = 0, TimeSpan? remaining = null)
        => StatusChanged?.Invoke(new KeyerStatus(state, message, currentRepeat, totalRepeats, remaining));

    private static bool IsSupportedVoiceMode(string? mode)
        => mode?.Equals("USB", StringComparison.OrdinalIgnoreCase) == true
            || mode?.Equals("LSB", StringComparison.OrdinalIgnoreCase) == true
            || mode?.Equals("DIGU", StringComparison.OrdinalIgnoreCase) == true
            || mode?.Equals("DIGL", StringComparison.OrdinalIgnoreCase) == true
            || mode?.Equals("DUSB", StringComparison.OrdinalIgnoreCase) == true
            || mode?.Equals("DLSB", StringComparison.OrdinalIgnoreCase) == true;

    private static void ValidateChrono(byte[] chrono)
    {
        var receiver = ReadU32(chrono, 0);
        var sampleRate = ReadU32(chrono, 4);
        var sampleType = ReadU32(chrono, 8);
        var length = ReadU32(chrono, 20);
        var channels = ReadU32(chrono, 28);
        if (receiver != 0 || sampleRate != 48000 || sampleType != 3 || channels != 2 || length == 0 || length > 4096 || length % 2 != 0)
            throw new InvalidOperationException($"Unexpected TX_CHRONO: receiver={receiver}, rate={sampleRate}, type={sampleType}, length={length}, channels={channels}");
    }

    private static byte[] BuildAudioPacket(byte[] chrono, float[] samples, ref int sampleIndex)
    {
        var receiver = ReadU32(chrono, 0);
        var sampleRate = ReadU32(chrono, 4);
        var length = ReadU32(chrono, 20);
        var valuesRequested = checked((int)length);
        var framesRequested = valuesRequested / 2;
        var packet = new byte[StreamHeaderBytes + valuesRequested * 4];

        WriteU32(packet, 0, receiver);
        WriteU32(packet, 4, sampleRate);
        WriteU32(packet, 8, 3);
        WriteU32(packet, 12, 0);
        WriteU32(packet, 16, 0);
        WriteU32(packet, 20, length);
        WriteU32(packet, 24, TxAudioStream);
        WriteU32(packet, 28, 2);

        for (var frame = 0; frame < framesRequested; frame++)
        {
            var value = sampleIndex < samples.Length ? samples[sampleIndex++] : 0f;
            var bits = BitConverter.SingleToInt32Bits(value);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(StreamHeaderBytes + frame * 8, 4), bits);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(StreamHeaderBytes + frame * 8 + 4, 4), bits);
        }

        return packet;
    }

    private static uint ReadU32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    private static void WriteU32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);

    private static int ApplyVolume(float[] samples, double volumePercent)
    {
        var gain = volumePercent / 100.0;
        var clippedFrames = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var scaled = samples[i] * gain;
            if (scaled > 1.0 || scaled < -1.0)
                clippedFrames++;
            samples[i] = (float)Math.Clamp(scaled, -1.0, 1.0);
        }
        return clippedFrames;
    }

    private static void ApplyFadeOut(float[] samples, int sampleRate, int milliseconds)
    {
        if (samples.Length == 0 || milliseconds <= 0) return;
        var fadeSamples = Math.Min(samples.Length, Math.Max(2, sampleRate * milliseconds / 1000));
        var start = samples.Length - fadeSamples;
        for (var i = 0; i < fadeSamples; i++)
        {
            var gain = 1f - i / (float)(fadeSamples - 1);
            samples[start + i] *= gain;
        }
        samples[^1] = 0f;
    }

    private static WavData LoadWav(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("WAV file not found.", path);

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
                format = br.ReadUInt16();
                channels = br.ReadUInt16();
                rate = br.ReadUInt32();
                br.ReadUInt32();
                br.ReadUInt16();
                bits = br.ReadUInt16();
            }
            else if (id == "data")
                data = br.ReadBytes(checked((int)size));
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
        for (var i = 0; i < frames; i++)
        {
            double sum = 0;
            for (var ch = 0; ch < channels; ch++)
            {
                var o = i * frameBytes + ch * bytesPerSample;
                float v;
                if (format == 3)
                    v = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(o, 4)));
                else if (bits == 16)
                    v = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(o, 2)) / 32768f;
                else if (bits == 24)
                {
                    var raw = data[o] | (data[o + 1] << 8) | (data[o + 2] << 16);
                    if ((raw & 0x800000) != 0) raw |= unchecked((int)0xFF000000);
                    v = raw / 8388608f;
                }
                else
                    v = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(o, 4)) / 2147483648f;
                sum += v;
            }
            mono[i] = (float)Math.Clamp(sum / channels, -1.0, 1.0);
        }

        return new WavData((int)rate, channels, mono);
    }

    public async ValueTask DisposeAsync()
    {
        if (IsRunning)
            await StopAsync();
        _socket?.Dispose();
        _runCts?.Dispose();
    }

    private sealed record WavData(int SampleRate, int SourceChannels, float[] MonoSamples)
    {
        public TimeSpan Duration => TimeSpan.FromSeconds(MonoSamples.Length / (double)SampleRate);
    }
}
