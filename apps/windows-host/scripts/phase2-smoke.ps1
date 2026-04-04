param(
    [int]$DeviceId = -1,
    [ValidateSet("WaveOut", "DebugDrain")]
    [string]$Mode = "WaveOut",
    [int]$DurationSeconds = 10,
    [int]$Port = 42100,
    [int]$TargetLatencyMs = 80,
    [string]$SignalMode = "sine",
    [string]$HostProject = "src/PhlanMic.WindowsHost",
    [string]$SenderProject = "src/PhlanMic.DebugTcpSender"
)

$ErrorActionPreference = "Stop"

if ($DurationSeconds -le 0) {
    throw "DurationSeconds must be greater than zero."
}

if ($Port -lt 1 -or $Port -gt 65535) {
    throw "Port must be between 1 and 65535."
}

if ($TargetLatencyMs -le 0) {
    throw "TargetLatencyMs must be greater than zero."
}

$root = Split-Path -Parent $PSScriptRoot
$artifactsDir = Join-Path $root "artifacts\phase2-smoke"
New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$hostStdoutLog = Join-Path $artifactsDir "host-$timestamp.stdout.log"
$hostStderrLog = Join-Path $artifactsDir "host-$timestamp.stderr.log"
$senderStdoutLog = Join-Path $artifactsDir "sender-$timestamp.stdout.log"
$senderStderrLog = Join-Path $artifactsDir "sender-$timestamp.stderr.log"
$summaryPath = Join-Path $artifactsDir "summary-$timestamp.json"

$framesToSend = [int](($DurationSeconds * 1000) / 20)
if ($framesToSend -le 0) {
    throw "Computed frame count must be greater than zero."
}

$env:PHLANMIC__RECEIVER__PORT = "$Port"
$env:PHLANMIC__OUTPUT__MODE = $Mode
$env:PHLANMIC__OUTPUT__DEVICEID = "$DeviceId"
$env:PHLANMIC__OUTPUT__TARGETLATENCYMS = "$TargetLatencyMs"
$env:PHLANMIC__OUTPUT__LOGAVAILABLEDEVICES = "true"

$hostProcess = $null

function Read-JsonLog {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Path
    )

    $events = New-Object System.Collections.Generic.List[object]

    foreach ($currentPath in $Path) {
        if (-not (Test-Path $currentPath)) {
            continue
        }

        foreach ($line in Get-Content -Path $currentPath) {
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }

            try {
                $events.Add(($line | ConvertFrom-Json))
            }
            catch {
            }
        }
    }

    return $events
}

function Stop-HostProcess {
    if ($null -ne $hostProcess -and -not $hostProcess.HasExited) {
        Stop-Process -Id $hostProcess.Id -ErrorAction SilentlyContinue
        $hostProcess.WaitForExit()
    }
}

try {
    $hostProcess = Start-Process `
        -FilePath "dotnet" `
        -ArgumentList @("run", "--project", $HostProject) `
        -WorkingDirectory $root `
        -RedirectStandardOutput $hostStdoutLog `
        -RedirectStandardError $hostStderrLog `
        -PassThru

    $startupDeadline = (Get-Date).AddSeconds(20)
    $hostReady = $false

    while ((Get-Date) -lt $startupDeadline) {
        if ($hostProcess.HasExited) {
            throw "Host exited before playback started. See $hostLog"
        }

        $events = Read-JsonLog -Path @($hostStdoutLog, $hostStderrLog)

        if ($events | Where-Object { $_.event -eq "host_start_failed" }) {
            throw "Host failed during startup. See $hostStdoutLog and $hostStderrLog"
        }

        if ($events | Where-Object { $_.event -eq "host_ready" }) {
            $hostReady = $true
            break
        }

        Start-Sleep -Milliseconds 250
    }

    if (-not $hostReady) {
        throw "Timed out waiting for host_ready. See $hostStdoutLog and $hostStderrLog"
    }

    $senderProcess = Start-Process `
        -FilePath "dotnet" `
        -ArgumentList @(
            "run",
            "--project",
            $SenderProject,
            "--",
            "--host",
            "127.0.0.1",
            "--port",
            "$Port",
            "--frames",
            "$framesToSend",
            "--mode",
            $SignalMode
        ) `
        -WorkingDirectory $root `
        -RedirectStandardOutput $senderStdoutLog `
        -RedirectStandardError $senderStderrLog `
        -PassThru `
        -Wait

    if ($senderProcess.ExitCode -ne 0) {
        throw "Sender exited with code $($senderProcess.ExitCode). See $senderStdoutLog and $senderStderrLog"
    }

    Start-Sleep -Seconds 2
    Stop-HostProcess

    $events = Read-JsonLog -Path @($hostStdoutLog, $hostStderrLog)
    if (-not $events.Count) {
        throw "Host log was empty. See $hostStdoutLog and $hostStderrLog"
    }

    if ($events | Where-Object { $_.event -eq "stream_session_faulted" }) {
        throw "Host entered faulted session state. See $hostStdoutLog and $hostStderrLog"
    }

    if ($events | Where-Object { $_.event -eq "host_start_failed" }) {
        throw "Host startup failure was logged. See $hostStdoutLog and $hostStderrLog"
    }

    if ($Mode -eq "WaveOut" -and -not ($events | Where-Object { $_.event -eq "audio_output_started" })) {
        throw "Playback never started. Expected audio_output_started after frames were sent. See $hostStdoutLog and $hostStderrLog"
    }

    $statsEvent = $events |
        Where-Object { $_.event -eq "stream_stats" } |
        Select-Object -Last 1

    if ($null -eq $statsEvent) {
        throw "No stream_stats event was found. See $hostStdoutLog and $hostStderrLog"
    }

    $completedFrames = [int64]$statsEvent.outputCompletedFrames
    $acceptedFrames = [int64]$statsEvent.acceptedFrames
    $faultCount = @($events | Where-Object { $_.event -eq "stream_session_faulted" }).Count
    $hostStartFailures = @($events | Where-Object { $_.event -eq "host_start_failed" }).Count
    $completedThreshold = [int64][Math]::Floor($framesToSend * 0.8)

    if ($acceptedFrames -le 0) {
        throw "No accepted frames were observed. See $hostStdoutLog and $hostStderrLog"
    }

    if ($completedFrames -le 0) {
        throw "No completed playback frames were observed. See $hostStdoutLog and $hostStderrLog"
    }

    if ($Mode -eq "WaveOut" -and $completedFrames -lt $completedThreshold) {
        throw "Completed playback frames $completedFrames were below the threshold $completedThreshold. See $hostStdoutLog and $hostStderrLog"
    }

    $summary = [ordered]@{
        mode = $Mode
        deviceId = $DeviceId
        port = $Port
        durationSeconds = $DurationSeconds
        framesSent = $framesToSend
        acceptedFrames = $acceptedFrames
        completedFrames = $completedFrames
        underrunCount = [int64]$statsEvent.underrunCount
        silenceFramesInserted = [int64]$statsEvent.silenceFramesInserted
        estimatedLatencyMs = [double]$statsEvent.estimatedLatencyMs
        glitchRatePerMinute = [double]$statsEvent.glitchRatePerMinute
        outputDeviceName = $statsEvent.outputDeviceName
        outputFormat = $statsEvent.outputFormat
        faultCount = $faultCount
        hostStartFailures = $hostStartFailures
        hostStdoutLog = $hostStdoutLog
        hostStderrLog = $hostStderrLog
        senderStdoutLog = $senderStdoutLog
        senderStderrLog = $senderStderrLog
    }

    $summary | ConvertTo-Json | Set-Content -Path $summaryPath

    Write-Host "Phase 2 smoke passed."
    Write-Host "Summary: $summaryPath"
    Write-Host "Host stdout log: $hostStdoutLog"
    Write-Host "Host stderr log: $hostStderrLog"
    Write-Host "Sender stdout log: $senderStdoutLog"
    Write-Host "Sender stderr log: $senderStderrLog"
    Write-Host "Completed frames: $completedFrames"
    Write-Host "Underruns: $($summary.underrunCount)"
    Write-Host "Estimated latency ms: $($summary.estimatedLatencyMs)"
}
finally {
    Stop-HostProcess
}
