# TCI Voice Keyer Architecture

## Goal

Build a reliable Windows voice keyer for Thetis that communicates entirely through TCI for control and audio. The finished application will play recorded CQ messages, key and release PTT automatically, repeat messages a selected number of times, and provide a simple graphical interface for non-expert users.

## Design principle

Keep the radio-control core separate from the user interface. The GUI must not implement TCI directly. It will call a tested keyer/core layer.

## Planned layers

1. **TCI Core**
   - WebSocket connection
   - TCI text command parser
   - RX/TX state tracking
   - PTT control using TRX
   - binary stream parsing
   - TX_CHRONO handling
   - TX_AUDIO_STREAM generation

2. **Voice Keyer Engine**
   - WAV loading
   - audio conversion/resampling
   - message playback state machine
   - repeat count
   - receive/listen delay
   - stop and fail-safe unkey behaviour

3. **GUI**
   - radio connection status
   - message selection
   - repeat count
   - listen delay
   - Start CQ / Stop controls
   - RX / TX / Error state
   - advanced settings hidden from normal users

## Milestones

### M1 - Read-only TCI connection diagnostic
Connect to Thetis, print incoming text commands and binary-frame sizes, and detect READY. This milestone must not key the transmitter.

### M2 - Controlled PTT diagnostic
Add explicit, short-duration PTT testing using `TRX:0,true,tci;` and guaranteed release using `TRX:0,false;`.

### M3 - TX_CHRONO diagnostic
Parse the TCI binary stream header and confirm the timing/sample requests produced by Thetis during TX.

### M4 - Silence transmission
Respond to TX_CHRONO with correctly structured TX_AUDIO_STREAM silence packets.

### M5 - Generated test tone
Transmit a known generated tone to prove audio sample formatting independent of WAV parsing.

### M6 - WAV voice transmission
Load and transmit one WAV voice message.

### M7 - Voice keyer sequence
Add message repeat count, RX pause, cancellation, and maximum TX timeout.

### M8 - Friendly Windows GUI
Add a simple non-expert interface without changing the proven core.

## Safety rules

- Never leave TX asserted following an error.
- PTT release is attempted on Stop, cancellation, disconnect, audio failure, timeout, and normal completion.
- The first milestone is read-only.
- Radio tests should initially be performed into a dummy load or with RF output otherwise made safe where practical.
