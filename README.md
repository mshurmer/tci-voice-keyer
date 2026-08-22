# TCI Voice Keyer

A Windows voice keyer for Thetis using the TCI protocol for both control and audio.

## Goal

The application will eventually:

- connect directly to Thetis using TCI over WebSocket;
- send voice audio using the TCI TX audio stream;
- control PTT with TCI (`TRX`);
- repeat a selected CQ voice message a configurable number of times;
- provide a simple graphical interface suitable for non-expert users;
- fail safe to receive if the connection, audio engine, or application fails.

## Development strategy

We are deliberately building and proving one small milestone at a time. The `main` branch is intended to remain a known-working baseline.

### Milestone 1 - TCI connection diagnostic ✅ PROVEN

The first diagnostic is read-only. It connects to Thetis, prints incoming TCI text messages, reports binary frames, and detects the final `READY;` initialization command.

Successfully tested against Thetis at `ws://127.0.0.1:50001/` with Thetis reporting ExpertSDR3 TCI protocol 2.0.

Observed audio defaults were 48 kHz, float32, stereo, 2048 samples, with 50 ms TX stream buffering.

### Milestone 2 - controlled PTT diagnostic

Next we will add an explicitly enabled, short-duration PTT test with guaranteed unkey logic. No voice audio will be sent yet.

## Run Milestone 1

```powershell
dotnet run --project src/TciVoiceKeyer.Console -- ws://127.0.0.1:50001/
```

Press Ctrl+C to stop.
