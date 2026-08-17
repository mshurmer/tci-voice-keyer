# Testing

## Milestone 1 - TCI connection diagnostic

This test is intentionally read-only. It does not send PTT or audio commands.

### Prerequisites

- Windows PC running Thetis with its TCI server enabled.
- .NET 8 SDK installed on the development PC.
- The TCI WebSocket address and port configured in Thetis.

### Run

From the repository root:

```powershell
dotnet run --project src/TciVoiceKeyer.Console -- ws://<thetis-ip>:<tci-port>
```

If Thetis is on the same PC, use the loopback address with the port configured in Thetis, for example:

```powershell
dotnet run --project src/TciVoiceKeyer.Console -- ws://127.0.0.1:<tci-port>
```

### Expected result

The program should:

1. report that the WebSocket connected;
2. print TCI initialization messages received from Thetis;
3. eventually detect `READY;`;
4. display `TCI READY received - Milestone 1 connection proven.`

Press Ctrl+C to stop.

### If it does not connect

Record the exact console output and check:

- TCI is enabled in Thetis;
- the address and port match Thetis' TCI settings;
- Windows Firewall is not blocking the connection;
- `ws://` rather than `http://` was supplied.

Do not proceed to PTT testing until Milestone 1 works reliably.
