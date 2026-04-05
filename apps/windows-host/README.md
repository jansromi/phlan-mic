# Windows Host

Projects:

- `src/PhlanMic.Host.Core`: transport-agnostic audio format, buffering, and stream pipeline primitives.
- `src/PhlanMic.WindowsHost`: Windows-only startup, config loading, and readable console logging with optional JSON mode.
- `src/PhlanMic.WindowsHost.Ui`: Phase 6 desktop shell for host readiness, manual connect details, output readiness, and troubleshooting.
- `src/PhlanMic.DebugTcpSender`: sender/test tool for exercising both the raw PCM TCP receiver and the Phase 5 UDP transport.
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
dotnet run --project src/PhlanMic.WindowsHost.Ui
```

App icon sources live under `assets/app-icons/`. After replacing the source images, regenerate the checked-in outputs with:

```sh
./scripts/generate-ios-app-icons.sh
python3 ./scripts/generate-windows-app-icon.py
```

The console host and desktop host now share the same runtime layer. The console app remains the best path for SSH/smoke-test flows, while the UI shows:

- host readiness and fault state
- local IP / manual connect details
- selected output target and VB-CABLE pairing state
- live session / stream counters
- troubleshooting diagnostics without parsing structured logs

When you launch `PhlanMic.WindowsHost.Ui` from `powershell.exe` or `cmd.exe`, the desktop window still opens and structured logs are also written back to that terminal session.

Environment overrides use the `PHLANMIC__` prefix. Example:

```powershell
$env:PHLANMIC__RECEIVER__PORT = "43000"
$env:PHLANMIC__LOGFORMAT = "Json"
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
dotnet run --project src/PhlanMic.DebugTcpSender -- --transport DebugTcpRawPcm --host 127.0.0.1 --port 42100 --frames 250 --mode sine
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
$env:PHLANMIC__RECEIVER__TRANSPORTMODE = "UdpRawPcm"
$env:PHLANMIC__RECEIVER__AUDIOPORT = "42101"
$env:PHLANMIC__RECEIVER__PAYLOADCODEC = "RawPcm16"
$env:PHLANMIC__RECEIVER__KEEPALIVEINTERVALMS = "1000"
$env:PHLANMIC__RECEIVER__SESSIONTIMEOUTMS = "5000"
$env:PHLANMIC__OUTPUT__MODE = "DebugDrain"
$env:PHLANMIC__OUTPUT__DEVICEID = "1"
$env:PHLANMIC__OUTPUT__ENDPOINTID = "{0.0.0.00000000}.{example-endpoint-guid}"
$env:PHLANMIC__OUTPUT__TARGETLATENCYMS = "60"
$env:PHLANMIC__LOGFORMAT = "Json"
```

Phase 3 VB-CABLE mode:

1. Install VB-CABLE on Windows.
2. Start the host in `VbCable` mode:

```powershell
cd apps/windows-host
$env:PHLANMIC__OUTPUT__MODE = "VbCable"
dotnet run --project src/PhlanMic.WindowsHost
```

Expected startup behavior:

- Startup logs emit `audio_endpoint_inventory` with Windows Core Audio render/capture endpoint ids, names, state, and default-role flags.
- If exactly one usable VB-CABLE pair is found, the host logs `vb_cable_endpoint_selected` and `audio_output_selected` with the selected render/capture endpoint ids.
- If VB-CABLE is missing, disabled, or ambiguous, startup fails with an actionable error explaining what to install, enable, or override.

If automatic matching is ambiguous, set an explicit render endpoint id:

```powershell
$env:PHLANMIC__OUTPUT__MODE = "VbCable"
$env:PHLANMIC__OUTPUT__ENDPOINTID = "{0.0.0.00000000}.{render-endpoint-guid}"
dotnet run --project src/PhlanMic.WindowsHost
```

Phase 2 smoke harness:

Run the Windows-only smoke script to start the host, wait for `host_ready`, stream a fixed-duration test signal, and assert from structured logs that playback started, robustness counters were emitted, and the session completed without a faulted host state:

The host defaults to human-readable text logs. The smoke script forces `PHLANMIC__LOGFORMAT=Json` so it can continue parsing exact event payloads.

```powershell
cd apps/windows-host
powershell -ExecutionPolicy Bypass -File .\scripts\phase2-smoke.ps1 -DeviceId 1 -DurationSeconds 10
```

Useful smoke-script options:

- `-Mode WaveOut` runs the real playback path and requires `audio_output_started` plus completed playback frames.
- `-Mode DebugDrain` runs the fallback sink and still validates host startup, sender completion, and stream stats.
- `-Mode VbCable` runs the endpoint-bound VB-CABLE path and validates the selected endpoint ids in the `audio_output_summary` event.
- `-TransportMode DebugTcpRawPcm|UdpRawPcm` selects the receiver/sender transport under test. `UdpRawPcm` also validates control-channel counters.
- `-Scenario Baseline|Pause|Burst|Reconnect` selects the sender behavior and the corresponding robustness assertions.
- `-EndpointId "{0.0.0.00000000}.{render-endpoint-guid}"` forces a specific VB-CABLE render endpoint when auto-detection is ambiguous.
- `-Port 43000` uses a non-default port if you need to avoid a conflict.
- `-AudioPort 43001` sets the UDP audio port when `-TransportMode UdpRawPcm` is used.
- `-TargetLatencyMs 60` lets you exercise a smaller playback target.
- `-StartupPrebufferFrames 4 -TargetBufferedFrames 3 -MaxLateFrameToleranceFrames 2 -MissingFrameGraceMs 20` overrides the host robustness config for tuning passes.
- `-SenderDelayMs 20` controls baseline sender cadence.
- `-DelayPatternMs "10,30"` drives the `Burst` scenario with a cyclic sender delay pattern.
- `-PauseAfterFrames 100 -PauseDurationMs 200` drives the `Pause` scenario without disconnecting the TCP sender.
- `-ReconnectPauseMs 300` controls the gap between sender runs in the `Reconnect` scenario.
- `-KeepAliveIntervalMs 1000 -SessionTimeoutMs 5000` tunes the Phase 5 UDP control session timing.
- `-DrainAfterSendMs 250` controls how long the script waits after the sender exits before stopping the host.

Phase 4 smoke assertions now include:

- startup playback only after the configured startup prebuffer is reached
- presence of robustness fields in `stream_stats` and `audio_output_summary`
- presence of transport counters in `stream_stats` and `audio_output_summary`
- scenario-specific checks for concealment, degradation, or reconnect counts
- continued rejection of faulted-session and startup-failure outcomes

Examples:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\phase2-smoke.ps1 -Mode VbCable -Scenario Baseline -DurationSeconds 10
powershell -ExecutionPolicy Bypass -File .\scripts\phase2-smoke.ps1 -Mode VbCable -Scenario Pause -PauseDurationMs 200
powershell -ExecutionPolicy Bypass -File .\scripts\phase2-smoke.ps1 -Mode VbCable -Scenario Burst -DelayPatternMs "10,30"
powershell -ExecutionPolicy Bypass -File .\scripts\phase2-smoke.ps1 -Mode VbCable -Scenario Reconnect -ReconnectPauseMs 300
powershell -ExecutionPolicy Bypass -File .\scripts\phase2-smoke.ps1 -Mode DebugDrain -TransportMode UdpRawPcm -Scenario Baseline -Port 42100 -AudioPort 42101
```

Phase 5 real-transport bring-up:

1. Start the host in `UdpRawPcm` mode:

```powershell
cd apps/windows-host
$env:PHLANMIC__RECEIVER__TRANSPORTMODE = "UdpRawPcm"
dotnet run --project src/PhlanMic.WindowsHost
```

2. In a second terminal, stream through the Phase 5 control + UDP path:

```powershell
cd apps/windows-host
dotnet run --project src/PhlanMic.DebugTcpSender -- --transport UdpRawPcm --host 127.0.0.1 --port 42100 --frames 250 --mode sine
```

Expected host behavior:

- `host_ready` includes `controlPort`, `audioPort`, `transportMode`, and `payloadCodec`.
- The session transitions to `Connected` after `hello`, then `Streaming` after the first UDP audio packet.
- `stream_stats` and `audio_output_summary` include transport counters such as `controlMessagesReceived`, `duplicatePackets`, `outOfOrderPackets`, `audioPacketsRejected`, and `decodeFailureCount`.

Artifacts are written under `artifacts\phase2-smoke\`:

- `host-*.stdout.log` / `host-*.stderr.log`: host output captured during the run
- `sender-*.stdout.log` / `sender-*.stderr.log`: debug sender output for each scenario run
- `summary-*.json`: parsed acceptance summary derived from exact `audio_output_summary` host events, including robustness counters and sender-run metadata
