# iOS Client Tasks

This document breaks the iPhone iOS client into implementation tasks for the MVP.

Current MVP assumptions:

- iOS client will target Swift and SwiftUI
- Microphone capture will use AVFoundation
- Network transport will start with a debug-friendly manual connection flow
- Initial streaming can assume foreground-only operation
- Windows host will be the receiving endpoint for the MVP

## Goal

Build an iPhone app that captures microphone audio and streams it to the Windows host with low enough latency for voice use.

## Toolchain

Recommended:

- Xcode
- Swift
- SwiftUI for basic app shell and status UI
- AVFoundation for microphone capture and audio session management
- Network.framework for LAN transport
- Bonjour/mDNS if automatic discovery is added during MVP

Build and testing targets:

- iPhone device first
- iOS simulator only for non-audio UI and session-flow checks
- Physical device testing for capture, networking, and latency validation

## Task Breakdown

### Phase 0: Client Foundation

1. Create the iOS app project structure.
2. Add a minimal app shell with connection status and microphone permission state.
3. Define the client-side audio format and packet model for MVP.
4. Add basic logging and diagnostics for capture and transport startup.
5. Establish a simple local configuration model for host address, port, and transport mode.

Definition of done:

- The app launches on an iPhone.
- The app can show basic status without streaming.
- The app builds a stable foundation for capture and networking work.

### Phase 1: Microphone Capture Spike

1. Request microphone permission.
2. Configure `AVAudioSession` for voice capture.
3. Capture mono microphone audio at the MVP format.
4. Convert captured audio into a stable frame model.
5. Add a simple input meter so capture quality can be inspected quickly.

Definition of done:

- The app can capture live microphone audio on a real device.
- Captured audio is framed consistently for transport.
- The app can report basic input level and permission failures.

### Phase 2: Debug Transport Prototype

1. Add a manual host address and port entry flow.
2. Send raw PCM frames to the Windows host debug receiver.
3. Add a simple connect, stream, stop, and reconnect lifecycle.
4. Surface transport errors clearly enough to debug LAN issues.
5. Verify the iPhone can continuously stream test audio to the host.

Definition of done:

- The app can stream live microphone audio to the Windows debug receiver.
- The stream can recover from simple disconnects.
- The app can tell the user whether the failure is capture, network, or host side.

### Phase 3: Session UX

1. Add a simple pairing or connection screen.
2. Show the selected Windows host and connection state.
3. Add mute, stream start/stop, and reconnect controls if needed.
4. Show local input level and network health in a compact status view.
5. Keep the first UI focused on clarity instead of polish.

Definition of done:

- A user can tell what the app is connected to.
- The user can start and stop streaming without force-closing the app.
- The app exposes enough state for day-to-day debugging.

### Phase 4: Discovery and Pairing

1. Decide whether the MVP uses manual IP entry, Bonjour discovery, or both.
2. If discovery is added, advertise the Windows host and list discovered devices.
3. Add a lightweight pairing or trust step if multiple hosts are possible.
4. Store the selected host or pairing token locally.
5. Keep fallback manual entry even if discovery is available.

Definition of done:

- The user can find or enter the Windows host without guessing.
- The app remembers the preferred host for the next session.
- Pairing or trust is understandable without a separate setup wizard.

### Phase 5: Transport Hardening

1. Add sequence numbers and packet timestamps on the client.
2. Add transport acknowledgements or control messages if the protocol needs them.
3. Add resilience for short network stalls and reconnect attempts.
4. Move toward the real transport format if raw PCM is only for debug bring-up.
5. Keep audio framing aligned with the Windows host jitter-buffer assumptions.

Definition of done:

- The client can tolerate small LAN hiccups without collapsing the session.
- The packet model is stable enough for the Windows host robustness layer.
- The transport path is ready to evolve away from debug-only PCM if needed.

### Phase 6: Background and Interruptions

1. Decide whether MVP requires background audio or foreground-only operation.
2. Handle interruptions, route changes, and permission changes cleanly.
3. Resume or stop streaming in a way that does not confuse the host.
4. Keep user messaging explicit when capture is paused or blocked.
5. Validate battery and thermal behavior during longer sessions.

Definition of done:

- The app behaves predictably across interruptions.
- The user gets clear feedback when streaming is paused or stopped.
- Long sessions remain usable on a real device.

### Phase 7: Release Readiness

1. Add crash-safe logging and diagnostics for support cases.
2. Decide on TestFlight-only MVP distribution or a broader release path.
3. Add onboarding text for microphone permission and local network requirements.
4. Add a small troubleshooting view for host discovery and connection failures.
5. Document setup expectations with the Windows host.

Definition of done:

- The app can be handed to a tester without extra verbal setup.
- The common setup failures are visible in the app.
- The release path is compatible with the Windows host MVP.

## Recommended Build Order

1. Phase 0: Client Foundation
2. Phase 1: Microphone Capture Spike
3. Phase 2: Debug Transport Prototype
4. Phase 3: Session UX
5. Phase 4: Discovery and Pairing
6. Phase 5: Transport Hardening
7. Phase 6: Background and Interruptions
8. Phase 7: Release Readiness

## First Sprint

These are the highest-value tasks to start immediately:

1. Create the iOS app project structure and app shell.
2. Configure microphone permission and voice-capture session setup.
3. Capture mono microphone frames at the MVP format.
4. Send raw PCM to the Windows debug receiver over the local network.
5. Add basic connection and capture diagnostics.

## Open iOS Questions

1. Should the MVP transport stay as raw PCM for bring-up, or move to Opus earlier?
2. Should discovery be manual IP first, Bonjour first, or both from the start?
3. Should the app be foreground-only for MVP, or should background audio be considered part of the first release?
4. What level of pairing or trust is needed before the Windows host is considered usable?
