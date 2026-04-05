param(
    [int]$DeviceId = -1,
    [ValidateSet("WaveOut", "DebugDrain", "VbCable")]
    [string]$Mode = "WaveOut",
    [ValidateSet("DebugTcpRawPcm", "UdpRawPcm")]
    [string]$TransportMode = "DebugTcpRawPcm",
    [string]$EndpointId = "",
    [ValidateSet("Baseline", "Pause", "Burst", "Reconnect")]
    [string]$Scenario = "Baseline",
    [int]$DurationSeconds = 10,
    [int]$DrainAfterSendMs = 250,
    [int]$Port = 42100,
    [int]$AudioPort = 42101,
    [int]$TargetLatencyMs = 80,
    [string]$SignalMode = "sine",
    [int]$SenderDelayMs = 20,
    [string]$DelayPatternMs = "",
    [int]$PauseAfterFrames = 0,
    [int]$PauseDurationMs = 0,
    [int]$ReconnectPauseMs = 250,
    [int]$StartupPrebufferFrames = 4,
    [int]$TargetBufferedFrames = 3,
    [int]$MaxLateFrameToleranceFrames = 2,
    [int]$MissingFrameGraceMs = 20,
    [bool]$ConcealMissingFramesWithSilence = $true,
    [int]$KeepAliveIntervalMs = 1000,
    [int]$SessionTimeoutMs = 5000,
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

if ($AudioPort -lt 1 -or $AudioPort -gt 65535) {
    throw "AudioPort must be between 1 and 65535."
}

if ($AudioPort -eq $Port -and $TransportMode -eq "UdpRawPcm") {
    throw "AudioPort must be different from Port when TransportMode is UdpRawPcm."
}

if ($DrainAfterSendMs -lt 0) {
    throw "DrainAfterSendMs must be zero or greater."
}

if ($SenderDelayMs -lt 0) {
    throw "SenderDelayMs must be zero or greater."
}

if ($ReconnectPauseMs -lt 0) {
    throw "ReconnectPauseMs must be zero or greater."
}

if ($StartupPrebufferFrames -le 0) {
    throw "StartupPrebufferFrames must be greater than zero."
}

if ($TargetBufferedFrames -le 0) {
    throw "TargetBufferedFrames must be greater than zero."
}

if ($MaxLateFrameToleranceFrames -lt 0) {
    throw "MaxLateFrameToleranceFrames must be zero or greater."
}

if ($MissingFrameGraceMs -lt 0) {
    throw "MissingFrameGraceMs must be zero or greater."
}

if ($KeepAliveIntervalMs -le 0) {
    throw "KeepAliveIntervalMs must be greater than zero."
}

if ($SessionTimeoutMs -le $KeepAliveIntervalMs) {
    throw "SessionTimeoutMs must be greater than KeepAliveIntervalMs."
}

$root = Split-Path -Parent $PSScriptRoot
$artifactsDir = Join-Path $root "artifacts\phase2-smoke"
New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$hostStdoutLog = Join-Path $artifactsDir "host-$timestamp.stdout.log"
$hostStderrLog = Join-Path $artifactsDir "host-$timestamp.stderr.log"
$summaryPath = Join-Path $artifactsDir "summary-$timestamp.json"

$framesToSend = [int](($DurationSeconds * 1000) / 20)
if ($framesToSend -le 0) {
    throw "Computed frame count must be greater than zero."
}

$env:PHLANMIC__RECEIVER__PORT = "$Port"
$env:PHLANMIC__RECEIVER__TRANSPORTMODE = $TransportMode
$env:PHLANMIC__OUTPUT__MODE = $Mode
$env:PHLANMIC__OUTPUT__DEVICEID = "$DeviceId"
$env:PHLANMIC__OUTPUT__TARGETLATENCYMS = "$TargetLatencyMs"
$env:PHLANMIC__LOGFORMAT = "Json"
$env:PHLANMIC__OUTPUT__LOGAVAILABLEDEVICES = "true"
$env:PHLANMIC__OUTPUT__LOGENDPOINTINVENTORY = "true"
$env:PHLANMIC__ROBUSTNESS__STARTUPPREBUFFERFRAMES = "$StartupPrebufferFrames"
$env:PHLANMIC__ROBUSTNESS__TARGETBUFFEREDFRAMES = "$TargetBufferedFrames"
$env:PHLANMIC__ROBUSTNESS__MAXLATEFRAMETOLERANCEFRAMES = "$MaxLateFrameToleranceFrames"
$env:PHLANMIC__ROBUSTNESS__MISSINGFRAMEGRACEMS = "$MissingFrameGraceMs"
$env:PHLANMIC__ROBUSTNESS__CONCEALMISSINGFRAMESWITHSILENCE = $ConcealMissingFramesWithSilence.ToString().ToLowerInvariant()
$env:PHLANMIC__RECEIVER__KEEPALIVEINTERVALMS = "$KeepAliveIntervalMs"
$env:PHLANMIC__RECEIVER__SESSIONTIMEOUTMS = "$SessionTimeoutMs"
$env:PHLANMIC__RECEIVER__PAYLOADCODEC = "RawPcm16"

if ($TransportMode -eq "UdpRawPcm") {
    $env:PHLANMIC__RECEIVER__AUDIOPORT = "$AudioPort"
}
else {
    Remove-Item Env:PHLANMIC__RECEIVER__AUDIOPORT -ErrorAction SilentlyContinue
}

if ([string]::IsNullOrWhiteSpace($EndpointId)) {
    Remove-Item Env:PHLANMIC__OUTPUT__ENDPOINTID -ErrorAction SilentlyContinue
}
else {
    $env:PHLANMIC__OUTPUT__ENDPOINTID = $EndpointId
}

$hostProcess = $null
$senderRuns = New-Object System.Collections.Generic.List[object]

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

function Get-PropertyValue {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Event,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $property = $Event.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Assert-EventHasProperties {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Event,
        [Parameter(Mandatory = $true)]
        [string[]]$PropertyNames,
        [Parameter(Mandatory = $true)]
        [string]$Context
    )

    foreach ($propertyName in $PropertyNames) {
        if ($null -eq $Event.PSObject.Properties[$propertyName]) {
            throw "Expected property '$propertyName' on $Context. See $hostStdoutLog and $hostStderrLog"
        }
    }
}

function Get-EventTimestampUtc {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Event
    )

    $timestampUtc = Get-PropertyValue -Event $Event -Name "timestampUtc"
    if ($null -eq $timestampUtc) {
        return $null
    }

    return [DateTimeOffset]::Parse($timestampUtc)
}

function Get-LatestOutputSummaryByScope {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Events,
        [Parameter(Mandatory = $true)]
        [string]$Scope
    )

    return $Events |
        Where-Object { $_.event -eq "audio_output_summary" -and $_.summaryScope -eq $Scope } |
        Select-Object -Last 1
}

function Stop-HostProcess {
    if ($null -ne $hostProcess -and -not $hostProcess.HasExited) {
        Stop-Process -Id $hostProcess.Id -ErrorAction SilentlyContinue
        $hostProcess.WaitForExit()
    }
}

function Invoke-SenderRun {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RunName,
        [Parameter(Mandatory = $true)]
        [int]$FrameCount,
        [int]$DelayMs = 20,
        [string]$DelayPattern = "",
        [int]$PauseAtFrames = 0,
        [int]$PauseMs = 0
    )

    $senderStdoutLog = Join-Path $artifactsDir "sender-$timestamp-$RunName.stdout.log"
    $senderStderrLog = Join-Path $artifactsDir "sender-$timestamp-$RunName.stderr.log"

    $arguments = @(
        "run",
        "--project",
        $SenderProject,
        "--",
        "--transport",
        $TransportMode,
        "--host",
        "127.0.0.1",
        "--port",
        "$Port",
        "--frames",
        "$FrameCount",
        "--mode",
        $SignalMode
    )

    if ([string]::IsNullOrWhiteSpace($DelayPattern)) {
        $arguments += @("--delay-ms", "$DelayMs")
    }
    else {
        $arguments += @("--delay-pattern-ms", $DelayPattern)
    }

    if ($PauseAtFrames -gt 0) {
        $arguments += @("--pause-after-frames", "$PauseAtFrames")
    }

    if ($PauseMs -gt 0) {
        $arguments += @("--pause-duration-ms", "$PauseMs")
    }

    $startedAtUtc = [DateTimeOffset]::UtcNow
    $senderProcess = Start-Process `
        -FilePath "dotnet" `
        -ArgumentList $arguments `
        -WorkingDirectory $root `
        -RedirectStandardOutput $senderStdoutLog `
        -RedirectStandardError $senderStderrLog `
        -PassThru `
        -Wait
    $finishedAtUtc = [DateTimeOffset]::UtcNow

    if ($senderProcess.ExitCode -ne 0) {
        throw "Sender run '$RunName' exited with code $($senderProcess.ExitCode). See $senderStdoutLog and $senderStderrLog"
    }

    return [pscustomobject]@{
        name = $RunName
        frameCount = $FrameCount
        delayMs = $DelayMs
        delayPatternMs = $DelayPattern
        pauseAfterFrames = $PauseAtFrames
        pauseDurationMs = $PauseMs
        startedAtUtc = $startedAtUtc
        finishedAtUtc = $finishedAtUtc
        stdoutLog = $senderStdoutLog
        stderrLog = $senderStderrLog
    }
}

function Invoke-ScenarioRuns {
    $runs = New-Object System.Collections.Generic.List[object]

    switch ($Scenario) {
        "Baseline" {
            $runs.Add((Invoke-SenderRun -RunName "baseline" -FrameCount $framesToSend -DelayMs $SenderDelayMs))
        }
        "Pause" {
            $effectivePauseAfterFrames = if ($PauseAfterFrames -gt 0) { $PauseAfterFrames } else { [Math]::Max(1, [int][Math]::Floor($framesToSend / 2)) }
            $effectivePauseDurationMs = if ($PauseDurationMs -gt 0) { $PauseDurationMs } else { [Math]::Max(160, $StartupPrebufferFrames * 40) }
            $runs.Add((Invoke-SenderRun -RunName "pause" -FrameCount $framesToSend -DelayMs $SenderDelayMs -PauseAtFrames $effectivePauseAfterFrames -PauseMs $effectivePauseDurationMs))
        }
        "Burst" {
            $effectiveDelayPattern = if ([string]::IsNullOrWhiteSpace($DelayPatternMs)) { "10,30" } else { $DelayPatternMs }
            $runs.Add((Invoke-SenderRun -RunName "burst" -FrameCount $framesToSend -DelayPattern $effectiveDelayPattern))
        }
        "Reconnect" {
            if ($framesToSend -lt 2) {
                throw "Reconnect scenario requires at least two frames."
            }

            $firstRunFrames = [Math]::Max(1, [int][Math]::Floor($framesToSend / 2))
            $secondRunFrames = [Math]::Max(1, $framesToSend - $firstRunFrames)

            $runs.Add((Invoke-SenderRun -RunName "reconnect-1" -FrameCount $firstRunFrames -DelayMs $SenderDelayMs))

            if ($ReconnectPauseMs -gt 0) {
                Start-Sleep -Milliseconds $ReconnectPauseMs
            }

            $runs.Add((Invoke-SenderRun -RunName "reconnect-2" -FrameCount $secondRunFrames -DelayMs $SenderDelayMs))
        }
        default {
            throw "Unsupported scenario '$Scenario'."
        }
    }

    return $runs
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
            throw "Host exited before host_ready. See $hostStdoutLog and $hostStderrLog"
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

    $hostReadyEvent = $events | Where-Object { $_.event -eq "host_ready" } | Select-Object -Last 1
    if ($null -eq $hostReadyEvent) {
        throw "host_ready event was not captured. See $hostStdoutLog and $hostStderrLog"
    }

    if ((Get-PropertyValue -Event $hostReadyEvent -Name "transportMode") -ne $TransportMode) {
        throw "host_ready transportMode '$((Get-PropertyValue -Event $hostReadyEvent -Name "transportMode"))' did not match requested transport '$TransportMode'. See $hostStdoutLog and $hostStderrLog"
    }

    if ($TransportMode -eq "UdpRawPcm") {
        $hostReadyAudioPort = [int](Get-PropertyValue -Event $hostReadyEvent -Name "audioPort")
        if ($hostReadyAudioPort -ne $AudioPort) {
            throw "host_ready audioPort $hostReadyAudioPort did not match requested AudioPort $AudioPort. See $hostStdoutLog and $hostStderrLog"
        }
    }

    foreach ($run in Invoke-ScenarioRuns) {
        $senderRuns.Add($run)
    }

    if ($DrainAfterSendMs -gt 0) {
        Start-Sleep -Milliseconds $DrainAfterSendMs
    }

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

    $outputStartedEvents = @($events | Where-Object { $_.event -eq "audio_output_started" })
    if ($Mode -ne "DebugDrain" -and $outputStartedEvents.Count -eq 0) {
        throw "Playback never started. Expected audio_output_started after frames were sent. See $hostStdoutLog and $hostStderrLog"
    }

    $disconnectSummary = Get-LatestOutputSummaryByScope -Events $events -Scope "disconnected"
    $shutdownSummary = Get-LatestOutputSummaryByScope -Events $events -Scope "shutdown"
    $summaryEvent = if ($null -ne $shutdownSummary) { $shutdownSummary } else { $disconnectSummary }

    if ($null -eq $summaryEvent) {
        throw "No audio_output_summary event was found. See $hostStdoutLog and $hostStderrLog"
    }

    $streamStatsEvents = @($events | Where-Object { $_.event -eq "stream_stats" })
    if ($streamStatsEvents.Count -eq 0) {
        throw "No stream_stats event was found. Smoke validation requires at least one periodic stats event. See $hostStdoutLog and $hostStderrLog"
    }

    $latestStreamStats = $streamStatsEvents[-1]
    $requiredRobustnessProperties = @(
        "streamRobustnessState",
        "expectedNextSequence",
        "highestReceivedSequence",
        "sequenceGapsObserved",
        "lateFramesArrived",
        "lateFramesDropped",
        "missingFramesDetected",
        "hostSilenceFramesInserted",
        "currentPrebufferDepth",
        "largestObservedGap",
        "startupPrebufferFrames",
        "targetBufferedFrames",
        "maxLateFrameToleranceFrames",
        "missingFrameGraceMs",
        "hostEstimatedBufferLatencyMs"
    )
    $requiredTransportProperties = @(
        "controlMessagesReceived",
        "controlMessagesSent",
        "controlTimeoutCount",
        "protocolErrorCount",
        "audioPacketsRejected",
        "duplicatePackets",
        "outOfOrderPackets",
        "decodeFailureCount"
    )
    Assert-EventHasProperties -Event $latestStreamStats -PropertyNames $requiredRobustnessProperties -Context "latest stream_stats event"
    Assert-EventHasProperties -Event $summaryEvent -PropertyNames $requiredRobustnessProperties -Context "audio_output_summary event"
    Assert-EventHasProperties -Event $latestStreamStats -PropertyNames $requiredTransportProperties -Context "latest stream_stats event"
    Assert-EventHasProperties -Event $summaryEvent -PropertyNames $requiredTransportProperties -Context "audio_output_summary event"

    if ($Mode -ne "DebugDrain") {
        $firstOutputStarted = $outputStartedEvents[0]
        Assert-EventHasProperties -Event $firstOutputStarted -PropertyNames @(
            "streamRobustnessState",
            "currentPrebufferDepth",
            "startupPrebufferFrames",
            "targetBufferedFrames",
            "missingFrameGraceMs"
        ) -Context "audio_output_started event"

        $startedPrebufferTarget = [int](Get-PropertyValue -Event $firstOutputStarted -Name "startupPrebufferFrames")
        $startedTargetBufferedFrames = [int](Get-PropertyValue -Event $firstOutputStarted -Name "targetBufferedFrames")
        $startedMissingFrameGraceMs = [int](Get-PropertyValue -Event $firstOutputStarted -Name "missingFrameGraceMs")
        $startedRobustnessState = [string](Get-PropertyValue -Event $firstOutputStarted -Name "streamRobustnessState")

        if ($startedPrebufferTarget -ne $StartupPrebufferFrames) {
            throw "audio_output_started reported startup prebuffer $startedPrebufferTarget, expected $StartupPrebufferFrames. See $hostStdoutLog and $hostStderrLog"
        }

        if ($startedTargetBufferedFrames -ne $TargetBufferedFrames) {
            throw "audio_output_started reported target buffered frames $startedTargetBufferedFrames, expected $TargetBufferedFrames. See $hostStdoutLog and $hostStderrLog"
        }

        if ($startedMissingFrameGraceMs -ne $MissingFrameGraceMs) {
            throw "audio_output_started reported missing frame grace $startedMissingFrameGraceMs, expected $MissingFrameGraceMs. See $hostStdoutLog and $hostStderrLog"
        }

        if ($startedRobustnessState -ne "Streaming") {
            throw "Playback started while streamRobustnessState was '$startedRobustnessState' instead of 'Streaming'. See $hostStdoutLog and $hostStderrLog"
        }
    }

    $completedFrames = [int64](Get-PropertyValue -Event $summaryEvent -Name "outputCompletedFrames")
    $acceptedFrames = [int64](Get-PropertyValue -Event $summaryEvent -Name "acceptedFrames")
    $faultCount = @($events | Where-Object { $_.event -eq "stream_session_faulted" }).Count
    $hostStartFailures = @($events | Where-Object { $_.event -eq "host_start_failed" }).Count
    $streamBufferDegradedCount = @($events | Where-Object { $_.event -eq "stream_buffer_degraded" }).Count
    $completedThreshold = [int64][Math]::Floor($framesToSend * 0.8)
    $disconnectObservedAtUtc = if ($null -ne $disconnectSummary) { Get-EventTimestampUtc -Event $disconnectSummary } else { $null }
    $activeSummary = if ($null -ne $disconnectSummary) { $disconnectSummary } else { $summaryEvent }
    $activeWindowAcceptedFrames = [int64](Get-PropertyValue -Event $activeSummary -Name "acceptedFrames")
    $activeWindowCompletedFrames = [int64](Get-PropertyValue -Event $activeSummary -Name "outputCompletedFrames")
    $activeWindowUnderrunCount = [int64](Get-PropertyValue -Event $activeSummary -Name "underrunCount")
    $postDisconnectUnderrunCount = [Math]::Max(0, [int64](Get-PropertyValue -Event $summaryEvent -Name "underrunCount") - $activeWindowUnderrunCount)
    $activeWindowCompletionRatio = if ($activeWindowAcceptedFrames -gt 0) { [double]$activeWindowCompletedFrames / [double]$activeWindowAcceptedFrames } else { 0.0 }
    $summaryHostSilenceFramesInserted = [int64](Get-PropertyValue -Event $summaryEvent -Name "hostSilenceFramesInserted")
    $summaryMissingFramesDetected = [int64](Get-PropertyValue -Event $summaryEvent -Name "missingFramesDetected")
    $summaryLateFramesDropped = [int64](Get-PropertyValue -Event $summaryEvent -Name "lateFramesDropped")
    $summarySequenceGapsObserved = [int64](Get-PropertyValue -Event $summaryEvent -Name "sequenceGapsObserved")
    $summaryConnectionCount = [int64](Get-PropertyValue -Event $summaryEvent -Name "connectionCount")
    $summaryControlMessagesReceived = [int64](Get-PropertyValue -Event $summaryEvent -Name "controlMessagesReceived")
    $summaryControlMessagesSent = [int64](Get-PropertyValue -Event $summaryEvent -Name "controlMessagesSent")
    $summaryControlTimeoutCount = [int64](Get-PropertyValue -Event $summaryEvent -Name "controlTimeoutCount")
    $summaryProtocolErrorCount = [int64](Get-PropertyValue -Event $summaryEvent -Name "protocolErrorCount")
    $summaryAudioPacketsRejected = [int64](Get-PropertyValue -Event $summaryEvent -Name "audioPacketsRejected")
    $summaryDuplicatePackets = [int64](Get-PropertyValue -Event $summaryEvent -Name "duplicatePackets")
    $summaryOutOfOrderPackets = [int64](Get-PropertyValue -Event $summaryEvent -Name "outOfOrderPackets")
    $summaryDecodeFailureCount = [int64](Get-PropertyValue -Event $summaryEvent -Name "decodeFailureCount")

    if ($acceptedFrames -le 0) {
        throw "No accepted frames were observed. See $hostStdoutLog and $hostStderrLog"
    }

    if ($completedFrames -le 0) {
        throw "No completed playback frames were observed. See $hostStdoutLog and $hostStderrLog"
    }

    if ($Mode -ne "DebugDrain" -and $completedFrames -lt $completedThreshold) {
        throw "Completed playback frames $completedFrames were below the threshold $completedThreshold. See $hostStdoutLog and $hostStderrLog"
    }

    if (-not [string]::IsNullOrWhiteSpace($EndpointId) -and (Get-PropertyValue -Event $summaryEvent -Name "outputEndpointId") -ne $EndpointId) {
        throw "Expected outputEndpointId '$EndpointId' but saw '$((Get-PropertyValue -Event $summaryEvent -Name "outputEndpointId"))'. See $hostStdoutLog and $hostStderrLog"
    }

    if ($summaryProtocolErrorCount -ne 0 -or $summaryAudioPacketsRejected -ne 0 -or $summaryDecodeFailureCount -ne 0 -or $summaryControlTimeoutCount -ne 0) {
        throw "Smoke run produced transport errors or timeouts unexpectedly. See $hostStdoutLog and $hostStderrLog"
    }

    if ($TransportMode -eq "UdpRawPcm" -and ($summaryControlMessagesReceived -le 0 -or $summaryControlMessagesSent -le 0)) {
        throw "UdpRawPcm smoke run did not observe control messages on the transport path. See $hostStdoutLog and $hostStderrLog"
    }

    switch ($Scenario) {
        "Baseline" {
            if ($streamBufferDegradedCount -gt 0) {
                throw "Baseline scenario emitted stream_buffer_degraded events. See $hostStdoutLog and $hostStderrLog"
            }

            if ($summaryHostSilenceFramesInserted -ne 0 -or $summaryMissingFramesDetected -ne 0 -or $summaryLateFramesDropped -ne 0 -or $summarySequenceGapsObserved -ne 0) {
                throw "Baseline scenario produced robustness error counters unexpectedly. See $hostStdoutLog and $hostStderrLog"
            }

            if ($summaryDuplicatePackets -ne 0 -or $summaryOutOfOrderPackets -ne 0) {
                throw "Baseline scenario produced duplicate or out-of-order transport counters unexpectedly. See $hostStdoutLog and $hostStderrLog"
            }
        }
        "Pause" {
            if ($summaryHostSilenceFramesInserted -le 0 -and $summaryMissingFramesDetected -le 0) {
                throw "Pause scenario did not produce host-side concealment counters. See $hostStdoutLog and $hostStderrLog"
            }
        }
        "Burst" {
            if ($streamBufferDegradedCount -gt 0) {
                throw "Burst scenario degraded instead of staying within the jitter buffer budget. See $hostStdoutLog and $hostStderrLog"
            }

            if ($summaryHostSilenceFramesInserted -ne 0 -or $summaryMissingFramesDetected -ne 0 -or $summarySequenceGapsObserved -ne 0) {
                throw "Burst scenario unexpectedly required concealment or gap handling. See $hostStdoutLog and $hostStderrLog"
            }
        }
        "Reconnect" {
            if ($summaryConnectionCount -lt 2) {
                throw "Reconnect scenario expected at least two stream connections but saw $summaryConnectionCount. See $hostStdoutLog and $hostStderrLog"
            }
        }
    }

    $summaryUnderrunCount = [int64](Get-PropertyValue -Event $summaryEvent -Name "underrunCount")
    $summarySinkSilenceFramesInserted = [int64](Get-PropertyValue -Event $summaryEvent -Name "silenceFramesInserted")
    $summaryLateFramesArrived = [int64](Get-PropertyValue -Event $summaryEvent -Name "lateFramesArrived")
    $summaryStreamRobustnessState = [string](Get-PropertyValue -Event $summaryEvent -Name "streamRobustnessState")
    $summaryCurrentPrebufferDepth = [int](Get-PropertyValue -Event $summaryEvent -Name "currentPrebufferDepth")
    $summaryLargestObservedGap = [int](Get-PropertyValue -Event $summaryEvent -Name "largestObservedGap")
    $summaryHostEstimatedBufferLatencyMs = [double](Get-PropertyValue -Event $summaryEvent -Name "hostEstimatedBufferLatencyMs")
    $summaryEstimatedLatencyMs = [double](Get-PropertyValue -Event $summaryEvent -Name "estimatedLatencyMs")
    $summaryGlitchRatePerMinute = [double](Get-PropertyValue -Event $summaryEvent -Name "glitchRatePerMinute")
    $summaryOutputSink = [string](Get-PropertyValue -Event $summaryEvent -Name "outputSink")
    $summaryOutputDeviceName = [string](Get-PropertyValue -Event $summaryEvent -Name "outputDeviceName")
    $summaryOutputEndpointId = [string](Get-PropertyValue -Event $summaryEvent -Name "outputEndpointId")
    $summaryOutputCaptureEndpointId = [string](Get-PropertyValue -Event $summaryEvent -Name "outputCaptureEndpointId")
    $summaryOutputCaptureEndpointName = [string](Get-PropertyValue -Event $summaryEvent -Name "outputCaptureEndpointName")
    $summaryOutputFormat = [string](Get-PropertyValue -Event $summaryEvent -Name "outputFormat")
    $summaryMissingFrameGraceMs = [int](Get-PropertyValue -Event $summaryEvent -Name "missingFrameGraceMs")
    $disconnectSummaryTimestampUtc = if ($null -ne $disconnectSummary) { Get-EventTimestampUtc -Event $disconnectSummary } else { $null }
    $shutdownSummaryTimestampUtc = if ($null -ne $shutdownSummary) { Get-EventTimestampUtc -Event $shutdownSummary } else { $null }
    $senderRunsArray = $senderRuns.ToArray()

    $summary = [ordered]@{
        scenario = $Scenario
        mode = $Mode
        transportMode = $TransportMode
        deviceId = $DeviceId
        endpointId = $EndpointId
        port = $Port
        audioPort = $AudioPort
        durationSeconds = $DurationSeconds
        drainAfterSendMs = $DrainAfterSendMs
        targetLatencyMs = $TargetLatencyMs
        senderDelayMs = $SenderDelayMs
        delayPatternMs = $DelayPatternMs
        pauseAfterFrames = $PauseAfterFrames
        pauseDurationMs = $PauseDurationMs
        reconnectPauseMs = $ReconnectPauseMs
        startupPrebufferFrames = $StartupPrebufferFrames
        targetBufferedFrames = $TargetBufferedFrames
        maxLateFrameToleranceFrames = $MaxLateFrameToleranceFrames
        missingFrameGraceMs = $MissingFrameGraceMs
        concealMissingFramesWithSilence = $ConcealMissingFramesWithSilence
        keepAliveIntervalMs = $KeepAliveIntervalMs
        sessionTimeoutMs = $SessionTimeoutMs
        framesSent = $framesToSend
        acceptedFrames = $acceptedFrames
        completedFrames = $completedFrames
        activeWindowAcceptedFrames = $activeWindowAcceptedFrames
        activeWindowCompletedFrames = $activeWindowCompletedFrames
        activeWindowCompletionRatio = $activeWindowCompletionRatio
        activeWindowUnderrunCount = $activeWindowUnderrunCount
        postDisconnectUnderrunCount = $postDisconnectUnderrunCount
        underrunCount = $summaryUnderrunCount
        sinkSilenceFramesInserted = $summarySinkSilenceFramesInserted
        hostSilenceFramesInserted = $summaryHostSilenceFramesInserted
        missingFramesDetected = $summaryMissingFramesDetected
        lateFramesDropped = $summaryLateFramesDropped
        lateFramesArrived = $summaryLateFramesArrived
        sequenceGapsObserved = $summarySequenceGapsObserved
        streamRobustnessState = $summaryStreamRobustnessState
        currentPrebufferDepth = $summaryCurrentPrebufferDepth
        largestObservedGap = $summaryLargestObservedGap
        hostEstimatedBufferLatencyMs = $summaryHostEstimatedBufferLatencyMs
        configuredMissingFrameGraceMs = $summaryMissingFrameGraceMs
        estimatedLatencyMs = $summaryEstimatedLatencyMs
        glitchRatePerMinute = $summaryGlitchRatePerMinute
        connectionCount = $summaryConnectionCount
        controlMessagesReceived = $summaryControlMessagesReceived
        controlMessagesSent = $summaryControlMessagesSent
        controlTimeoutCount = $summaryControlTimeoutCount
        protocolErrorCount = $summaryProtocolErrorCount
        audioPacketsRejected = $summaryAudioPacketsRejected
        duplicatePackets = $summaryDuplicatePackets
        outOfOrderPackets = $summaryOutOfOrderPackets
        decodeFailureCount = $summaryDecodeFailureCount
        streamBufferDegradedCount = $streamBufferDegradedCount
        outputSink = $summaryOutputSink
        outputDeviceName = $summaryOutputDeviceName
        outputEndpointId = $summaryOutputEndpointId
        outputCaptureEndpointId = $summaryOutputCaptureEndpointId
        outputCaptureEndpointName = $summaryOutputCaptureEndpointName
        outputFormat = $summaryOutputFormat
        disconnectObservedAtUtc = $disconnectObservedAtUtc
        disconnectSummaryTimestampUtc = $disconnectSummaryTimestampUtc
        shutdownSummaryTimestampUtc = $shutdownSummaryTimestampUtc
        faultCount = $faultCount
        hostStartFailures = $hostStartFailures
        hostStdoutLog = $hostStdoutLog
        hostStderrLog = $hostStderrLog
        senderRuns = $senderRunsArray
    }

    $summary | ConvertTo-Json -Depth 6 | Set-Content -Path $summaryPath

    Write-Host "Windows smoke passed."
    Write-Host "Scenario: $Scenario"
    Write-Host "Summary: $summaryPath"
    Write-Host "Host stdout log: $hostStdoutLog"
    Write-Host "Host stderr log: $hostStderrLog"
    Write-Host "Completed frames: $completedFrames"
    Write-Host "Active window completed/accepted: $activeWindowCompletedFrames / $activeWindowAcceptedFrames"
    Write-Host "Host silence insertions: $summaryHostSilenceFramesInserted"
    Write-Host "Missing frames detected: $summaryMissingFramesDetected"
    Write-Host "Late frames dropped: $summaryLateFramesDropped"
    Write-Host "Stream buffer degraded events: $streamBufferDegradedCount"
    Write-Host "Underruns: $($summary.underrunCount)"
    Write-Host "Estimated latency ms: $($summary.estimatedLatencyMs)"
}
finally {
    Stop-HostProcess
}
