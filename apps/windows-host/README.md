# Windows Host

Projects:

- `src/PhlanMic.Host.Core`: transport-agnostic audio format, buffering, and stream pipeline primitives.
- `src/PhlanMic.WindowsHost`: Windows-only startup, config loading, and structured console logging.
- `src/PhlanMic.DebugTcpSender`: small debug sender for exercising the raw PCM TCP receiver.
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

End-to-end Phase 1 test:

1. Start the host:

```powershell
cd apps/windows-host
dotnet run --project src/PhlanMic.WindowsHost
```

2. In a second terminal, send a short test stream:

```powershell
cd apps/windows-host
dotnet run --project src/PhlanMic.DebugTcpSender -- --host 127.0.0.1 --port 42100 --frames 250 --mode sine
```

Expected host behavior:

- Session transitions through `Listening`, `Connected`, `Streaming`, `Disconnected`, then back to `Listening`.
- `bytesReceived`, `framesReceived`, `acceptedFrames`, `drainedFrames`, and `drainedBytes` increase during the run.
- `bufferedFrames` should stay low because the temporary debug drain continuously reads from the pipeline.

Phase 2 local playback test on Windows:

1. Start the host with speaker playback enabled:

```powershell
cd apps/windows-host
dotnet run --project src/PhlanMic.WindowsHost
```

2. In a second terminal, stream debug audio:

```powershell
cd apps/windows-host
dotnet run --project src/PhlanMic.DebugTcpSender -- --host 127.0.0.1 --port 42100 --frames 500 --mode sine
```

Expected host behavior:

- Startup logs enumerate available `waveOut` devices and show the selected device id and negotiated output format.
- The incoming sine stream should play through the selected speaker/headphone device.
- `outputSubmittedFrames`, `outputCompletedFrames`, `estimatedLatencyMs`, and `glitchRatePerMinute` update in the periodic stats logs.
- `audio_output_underrun` and `audio_output_overrun` warnings should only appear when the pipeline starves or overfills.

Useful environment overrides:

```powershell
$env:PHLANMIC__OUTPUT__MODE = "DebugDrain"
$env:PHLANMIC__OUTPUT__DEVICEID = "1"
$env:PHLANMIC__OUTPUT__TARGETLATENCYMS = "60"
```
