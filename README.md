# TCI Voice Keyer

A Windows voice keyer for Thetis using TCI for both radio control and transmit/receive audio.

The project is being developed in small, testable milestones so that each proven stage remains recoverable in GitHub.

## Current milestone

**Milestone 1: read-only TCI connection diagnostic**

The current diagnostic connects to a TCI WebSocket server, displays initialization traffic, reports binary frame sizes, and confirms when `READY;` is received. It intentionally does **not** key the transmitter or send audio.

## Run the diagnostic

Requires the .NET 8 SDK.

```powershell
dotnet run --project src/TciVoiceKeyer.Console -- ws://<thetis-ip>:<tci-port>
```

For Thetis running on the same PC:

```powershell
dotnet run --project src/TciVoiceKeyer.Console -- ws://127.0.0.1:<tci-port>
```

Use the TCI port shown/configured in Thetis.

See [docs/testing.md](docs/testing.md) for the test procedure and [docs/architecture.md](docs/architecture.md) for the planned milestones.

## Planned end state

The finished application is intended to provide a simple graphical interface with:

- connection status;
- selectable recorded voice messages;
- repeat count;
- receive/listen delay;
- Start CQ and Stop controls;
- clear RX/TX/Error indication;
- fail-safe PTT release.

The GUI will remain separate from the tested TCI core so that interface changes cannot easily break radio-control logic.
