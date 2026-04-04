# Windows Host Foundation

Phase 0 lives here.

Projects:

- `src/PhlanMic.Host.Core`: transport-agnostic audio format, buffering, and stream pipeline primitives.
- `src/PhlanMic.WindowsHost`: Windows-only startup, config loading, and structured console logging.
- `tests/PhlanMic.Host.Core.Tests`: core tests with no Windows-only dependencies.

Current internal audio format for MVP:

- 48 kHz
- Mono
- 16-bit signed PCM
- 20 ms frames (`960` samples / `1920` bytes per frame)

Build on Windows with the .NET 8 SDK:

```powershell
cd apps/windows-host
dotnet build PhlanMic.WindowsHost.sln
dotnet test PhlanMic.WindowsHost.sln
dotnet run --project src/PhlanMic.WindowsHost
```

Environment overrides use the `PHLANMIC__` prefix. Example:

```powershell
$env:PHLANMIC__RECEIVER__PORT = "43000"
dotnet run --project src/PhlanMic.WindowsHost
```

