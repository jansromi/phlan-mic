# iOS Client Status Snapshot

Date: 2026-04-11

This document summarizes the current state of the iOS client based on:

- the checked-in Swift code
- iOS-focused commit history on `dev`
- implementation notes in `.codex/`
- the current tests and transport protocol code

It is intended to answer one question clearly:

What does the iPhone client actually do today, and what remains unfinished?

## Executive Summary

The iOS client is no longer a shell or placeholder.

It currently includes:

- a SwiftUI app shell
- microphone permission handling
- live microphone capture on device
- fixed-shape PCM framing aligned with the Windows host
- a debug TCP streaming path
- a realtime `UdpRawPcm` transport using:
  - TCP control messages
  - UDP audio packets
- session health and transport diagnostics
- scene/lifecycle handling for app deactivation and audio-session events

The current iOS app is best described as:

- a working developer-facing client for the PhlanMic prototype
- able to participate in end-to-end streaming
- still more diagnostic/bring-up oriented than product polished

The biggest remaining gaps are:

- no discovery or pairing flow
- no polished consumer-facing session UX
- no final background continuation policy
- no productized reconnect strategy

## Current Repo-Level Reality

The current repo state contradicts older iOS planning docs in a few important ways.

What is definitely implemented in code:

- `HostConfiguration.TransportMode` supports both `tcpDebug` and `udpRealtime`
- `AppModel` can create and drive both transport modes
- `UdpRawPcmTransportClient` exists and is integrated
- `TransportProtocol.swift` exists with control-message and audio-packet serialization
- `PhlanMicIOSClientApp` observes `scenePhase`
- `MicrophoneCaptureClient` emits structured session events upward
- tests explicitly exercise realtime transport and lifecycle behavior

This means the iOS client has moved well beyond the older point where `udpRealtime` was only planned.

## Architecture Snapshot

### App Layer

Main app/UI pieces:

- `apps/ios/PhlanMic.iOSClient/App/PhlanMicIOSClientApp.swift`
- `apps/ios/PhlanMic.iOSClient/App/AppModel.swift`
- `apps/ios/PhlanMic.iOSClient/App/ContentView.swift`

Current structure:

- `PhlanMicIOSClientApp` owns the top-level `AppModel`
- `scenePhase` changes are forwarded into `AppModel`
- `AppModel` is the orchestration boundary for:
  - permissions
  - capture lifecycle
  - transport lifecycle
  - user-visible status
  - diagnostics

That is the right seam for the current app shape. Capture and transport remain separate components, while policy stays in the app model.

### Audio Capture

Primary capture file:

- `apps/ios/PhlanMic.iOSClient/Audio/MicrophoneCaptureClient.swift`

Current capture behavior:

- uses `AVAudioSession` and `AVAudioEngine`
- requests mono PCM output matching the host MVP format
- uses `AVAudioConverter` to normalize device input into transport-ready PCM
- emits framed `CapturedAudioFrame` values
- emits input metering derived from the converted PCM
- emits structured capture/session events upward

Current audio assumptions:

- `48 kHz`
- mono
- `16-bit` signed PCM
- `20 ms` packet cadence

Important current configuration choice:

- the session is configured as `.record` with `.measurement`

That change is significant. Earlier bring-up used voice-chat-oriented processing, but the later iOS debugging notes indicate that `record + measurement` is the better fit for PhlanMic because the app uplinks microphone audio and does not need bidirectional voice-chat DSP.

### Transport

Relevant files:

- `apps/ios/PhlanMic.iOSClient/Networking/DebugTcpPcmClient.swift`
- `apps/ios/PhlanMic.iOSClient/Networking/UdpRawPcmTransportClient.swift`
- `apps/ios/PhlanMic.iOSClient/Networking/TransportProtocol.swift`
- `apps/ios/PhlanMic.iOSClient/Networking/AudioTransportClient.swift`
- `apps/ios/PhlanMic.iOSClient/Networking/HostConfiguration.swift`

Two transport modes exist:

- `TCP Debug`
- `UDP Realtime`

Current default configuration in `HostConfiguration` is:

- host: `192.168.50.18`
- port: `42100`
- transport mode: `udpRealtime`

That default is useful for local bring-up, but it is a developer default, not a product-ready discovery flow.

#### Debug TCP Path

The debug path is still valuable because it remains the simplest end-to-end bring-up mode.

It provides:

- a single TCP connection
- raw PCM byte writes with no packet envelope
- compatibility with the host-side `DebugTcpRawPcm` receiver

This path is still useful as:

- a regression fallback
- a simpler troubleshooting path when realtime protocol behavior is not the thing being tested

#### UDP Realtime Path

The realtime path is implemented, not stubbed.

Current behavior in `UdpRawPcmTransportClient`:

- open a TCP control connection to the Windows host
- send `hello`
- handle `helloAccepted`
- negotiate the host-provided UDP audio port
- send `startStream`
- handle `startAccepted`
- start periodic `keepAlive` traffic
- send audio frames as versioned UDP packets
- send `stopStream` on orderly disconnect

Current protocol assumptions:

- control messages are versioned
- audio packets are versioned
- payload codec is currently `RawPcm16`
- audio packets carry:
  - session id
  - codec
  - sequence number
  - capture timestamp
  - payload bytes

This is a meaningful step beyond the original debug transport and is aligned with the Windows host’s realtime path.

## Current App Behavior

### Primary User Flow

The app currently supports this high-level flow:

1. validate host configuration
2. request microphone permission if needed
3. connect the selected transport
4. begin capture when the transport is ready
5. stream framed mic audio until user stop or system/transport interruption

The app model explicitly tracks:

- setup readiness
- capture status
- transport status
- transport counters
- session health summary
- last transport error
- lifecycle-driven stop reasons

This is no longer just "tap a button and hope the socket works." The state model is substantial enough to diagnose whether failure happened in:

- setup
- transport connection
- control handshake
- audio send
- capture lifecycle

### Session Health Surface

Recent iOS commits focused heavily on the session-health UX.

Current status surfaces include:

- connection state
- sent-frame count
- last successful send time
- keepalive timing
- control-message activity
- transport detail text
- error state

This is still a debug-oriented product surface, but it is materially better than the original raw diagnostics shell.

## Lifecycle Hardening Status

This is one of the most important areas where the iOS client has clearly advanced.

The current codebase includes:

- `scenePhase` observation in `PhlanMicIOSClientApp`
- `handleScenePhaseChange(_:)` in `AppModel`
- `CaptureSessionEvent` types in the app model
- interruption observation in `MicrophoneCaptureClient`
- route-change observation in `MicrophoneCaptureClient`
- system stop reasons such as:
  - scene became inactive
  - scene entered background
  - audio interrupted
  - route invalidated
  - capture failed
  - transport failed

This means the app has moved beyond the earlier assumption that the only stop causes are:

- user tapped stop
- transport failed

That is a real maturity improvement because iOS lifecycle behavior is one of the central risk areas for this product.

### What The Current Lifecycle Policy Appears To Be

Based on the implementation plan, tests, and current app model, the client follows a conservative policy:

- when the app becomes inactive or backgrounded, stop the active session
- when audio is interrupted, stop the active session cleanly
- when route changes invalidate usable input, stop the active session
- do not blindly auto-resume after interruptions

That is the right policy for the current maturity level because it keeps failures diagnosable and avoids pretending that background continuation is already solved.

## Validation Evidence

The strongest evidence for the current iOS state comes from three places:

- `.codex` worknotes
- recent iOS commits
- current tests

### Validation Reported In Notes

Earlier `.codex` notes report:

- simulator builds succeeded during Phase 1 and Phase 2 bring-up
- real-device validation succeeded for microphone capture and debug TCP streaming
- later realtime UDP runs completed end to end

The iOS UDP debugging writeup also documents an important conclusion:

- the realtime transport itself eventually proved healthy
- the largest audio-quality issue was likely iPhone capture/session configuration, not only transport

That writeup points to the later healthy state as:

- `record + measurement` on the iPhone
- host counters showing:
  - `rejected=0`
  - `late=0/0`
  - `missing=0`
  - `silence=0`

### Current Test Surface

The current tests indicate the client has a non-trivial model surface and protocol surface.

Relevant tests:

- `apps/ios/PhlanMic.iOSClientTests/AppModelTests.swift`
- `apps/ios/PhlanMic.iOSClientTests/TransportProtocolTests.swift`

The checked-in tests cover at least:

- permission-before-connect behavior
- mode selection between TCP debug and UDP realtime
- control message progression through realtime setup
- keepalive reporting
- session-health derived state
- scene-inactive stop behavior
- interruption handling
- route-change handling
- protocol serialization/deserialization

That is important because it means the current iOS client is not only manually tested UI code. Some of the core orchestration and protocol behavior is pinned by unit tests.

## Recent Commit Story

The iOS commit history on `dev` shows a clear progression:

- `ac6df74`
  core iOS shell, permissions, audio models, host configuration
- `4dccb1c`
  live microphone capture and input-level monitoring
- `fd84275`
  major UI refactor and debug TCP client
- `ab5c62a` and `b2d185c`
  session-health monitoring and UI
- `d0e0837`
  realtime `UdpRawPcmTransportClient` and protocol work
- `17d4a11`
  transport parameter improvements
- `8e73a27` and `e82e401`
  choppy-audio debugging and audio-session/default updates
- `ff48bbc`
  scene-state management and capture session event handling
- `3301600`
  stronger app-model status and UI behavior
- `a4af84b`
  startup styling simplification
- `7ea52d7`
  session-health and connection-status polish

That commit sequence reads like a real vertical-slice evolution:

- build capture
- build debug transport
- build realtime transport
- debug audio quality
- harden lifecycle behavior
- polish the UX

## Quality And Product Readiness Assessment

The iOS client is technically meaningful, but not product-ready.

What is strong already:

- transport and capture are separated cleanly
- realtime protocol exists
- lifecycle state is explicit
- diagnostics are much stronger than a typical prototype
- there is direct evidence of end-to-end operation

What still feels prototype-level:

- manual host entry is still the normal flow
- the UI remains fairly diagnostic-heavy
- reconnect/product behavior is not yet a finalized user experience
- background continuation is not settled as a supported feature
- local defaults are tuned for one development environment, not for a general install experience

## Known Gaps And Risks

The main iOS gaps still visible from code and notes are:

- no discovery flow
- no pairing flow
- no saved-host or polished host-selection model
- no final reconnect policy
- no final background-support decision
- no first-run UX aimed at non-developer users
- no codec evolution beyond raw PCM on the iOS side

The most important technical/product risk remains:

- whether the app can or should support meaningful locked-screen or background streaming on iOS without changing the product shape to fit a different Apple platform model

## Bottom-Line Assessment

The current iOS client is a working prototype client with real transport, real capture, and meaningful lifecycle handling.

The most accurate concise summary is:

- realtime transport exists
- end-to-end streaming has been exercised
- the app model is substantially more mature than the older docs suggest
- the remaining work is mostly productization, UX simplification, and platform-policy decisions rather than initial feasibility

## Primary Source Files Used For This Snapshot

Main local sources used for this writeup:

- [apps/ios/PhlanMic.iOSClient/App/AppModel.swift](../apps/ios/PhlanMic.iOSClient/App/AppModel.swift)
- [apps/ios/PhlanMic.iOSClient/App/PhlanMicIOSClientApp.swift](../apps/ios/PhlanMic.iOSClient/App/PhlanMicIOSClientApp.swift)
- [apps/ios/PhlanMic.iOSClient/Audio/MicrophoneCaptureClient.swift](../apps/ios/PhlanMic.iOSClient/Audio/MicrophoneCaptureClient.swift)
- [apps/ios/PhlanMic.iOSClient/Networking/HostConfiguration.swift](../apps/ios/PhlanMic.iOSClient/Networking/HostConfiguration.swift)
- [apps/ios/PhlanMic.iOSClient/Networking/UdpRawPcmTransportClient.swift](../apps/ios/PhlanMic.iOSClient/Networking/UdpRawPcmTransportClient.swift)
- [apps/ios/PhlanMic.iOSClient/Networking/TransportProtocol.swift](../apps/ios/PhlanMic.iOSClient/Networking/TransportProtocol.swift)
- [apps/ios/PhlanMic.iOSClientTests/AppModelTests.swift](../apps/ios/PhlanMic.iOSClientTests/AppModelTests.swift)
- [apps/ios/PhlanMic.iOSClientTests/TransportProtocolTests.swift](../apps/ios/PhlanMic.iOSClientTests/TransportProtocolTests.swift)
- [.codex/phase1-ios-worknotes.txt](../.codex/phase1-ios-worknotes.txt)
- [.codex/phase2-ios-worknotes.txt](../.codex/phase2-ios-worknotes.txt)
- [.codex/2026-04-05-ios-lifecycle-hardening-implementation-plan.md](../.codex/2026-04-05-ios-lifecycle-hardening-implementation-plan.md)
- [.codex/issues/2026-04-05-ios-udp-choppy-audio-writeup.md](../.codex/issues/2026-04-05-ios-udp-choppy-audio-writeup.md)
