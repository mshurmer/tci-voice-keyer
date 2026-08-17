# Milestone 2 - Guarded PTT diagnostic

This test proves that Thetis accepts TCI PTT control and lets us observe what it sends while TCI is selected as the TX audio source.

## Safety design

The diagnostic:

- waits for `ready;`;
- refuses to key unless it observed `tx_enable:0,true;`;
- requires the operator to type exactly `KEY`;
- performs a 3-second countdown;
- sends `trx:0,true,tci;` only once;
- limits the requested transmit window to 750 ms;
- sends `trx:0,false;` immediately afterward;
- attempts another `trx:0,false;` during cleanup if an exception occurs after keying;
- sends no TX audio samples in this milestone.

Because this command can cause RF transmission, use a dummy load where available or otherwise make sure a brief transmit event is safe and lawful.

## Run

```powershell
git fetch
git checkout feature/ptt-diagnostic
git pull

dotnet run --project src/TciVoiceKeyer.PttDiagnostic -- ws://127.0.0.1:50001/
```

The program first connects without transmitting. Only after it confirms TCI readiness and TX permission will it offer the `KEY` confirmation prompt.

## What to record

Copy the complete console output after the test, especially:

- any `trx:0,true;` or `trx:0,false;` notifications;
- every `BINARY frame while keyed: ... bytes` line;
- whether Thetis visibly entered TX and returned to RX;
- whether any unexpected RF/audio behaviour occurred.

Do not repeat the test if Thetis fails to return to RX. Manually release MOX/PTT first and investigate.
