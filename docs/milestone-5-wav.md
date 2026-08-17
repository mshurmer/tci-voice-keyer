# Milestone 5 - WAV voice playback

Goal: prove that a prerecorded voice WAV can be supplied to Thetis over the same TCI TX audio path already proven with digital silence and a generated tone.

## Safety and scope

- Receiver/transceiver 0 must report `tx_enable:0,true;`.
- Receiver 0 must be in USB mode.
- Operator must type exactly `WAV` before PTT is sent.
- Maximum accepted WAV duration is 30 seconds.
- The program attempts `trx:0,false;` at the end and from cleanup if an error occurs after keying.
- If MOX fails to release automatically, manually unkey and do not repeat until reviewed.

## WAV format for this first live test

To avoid adding resampling as another variable, Milestone 5 requires exactly 48 kHz. It supports mono or stereo RIFF/WAVE files containing:

- PCM 16-bit
- PCM 24-bit
- PCM 32-bit
- IEEE float 32-bit

Stereo input is averaged to mono, then duplicated into both TCI float32 TX channels. Later milestones can add automatic resampling and broader WAV handling.

## Run

```powershell
dotnet run --project src/TciVoiceKeyer.WavDiagnostic -- ws://127.0.0.1:50001/ "C:\path\cq.wav"
```

The diagnostic prints the WAV sample rate, channels, sample count, duration, and peak level before making any connection/PTT attempt. A non-48-kHz file is refused without transmitting.
