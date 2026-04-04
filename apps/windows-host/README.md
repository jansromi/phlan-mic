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

Phase 2 smoke harness:

Run the Windows-only smoke script to start the host, wait for `host_ready`, stream a fixed-duration sine signal, and assert from structured logs that playback started and completed without a faulted session:

```powershell
cd apps/windows-host
powershell -ExecutionPolicy Bypass -File .\scripts\phase2-smoke.ps1 -DeviceId 1 -DurationSeconds 10
```

Useful smoke-script options:

- `-Mode WaveOut` runs the real playback path and requires `audio_output_started` plus completed playback frames.
- `-Mode DebugDrain` runs the fallback sink and still validates host startup, sender completion, and stream stats.
- `-Port 43000` uses a non-default port if you need to avoid a conflict.
- `-TargetLatencyMs 60` lets you exercise a smaller playback target.
- `-DrainAfterSendMs 250` controls how long the script waits after the sender exits before stopping the host.

Artifacts are written under `artifacts\phase2-smoke\`:

- `host-*.stdout.log` / `host-*.stderr.log`: host output captured during the run
- `sender-*.stdout.log` / `sender-*.stderr.log`: debug sender output
- `summary-*.json`: parsed acceptance summary derived from exact `audio_output_summary` host events, including disconnect/shutdown counters
