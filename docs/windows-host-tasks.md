# Windows Host Tasks

This document breaks the Windows host software into implementation tasks for the MVP.

Current MVP assumptions:

- Windows host will target `.NET 8`
- Virtual audio path will use `VB-CABLE`
- Initial connection flow will use manual IP/session setup
- Initial iOS integration can assume foreground-only streaming

## Goal

Build a Windows app that receives microphone audio from the iOS client and makes it available to Windows apps through VB-CABLE.

## Task Breakdown

### Phase 0: Host Foundation

1. Create the Windows host solution structure.
2. Create a `core` library for protocol, buffering, and stream pipeline code.
3. Create a `host` app for Windows-only startup, config, and device integration.
4. Add structured logging and a simple config model.
5. Define the internal audio format for MVP.

Definition of done:

- The solution builds on Windows.
- The host can start and load config.
- The core can be tested independently of VB-CABLE.

### Phase 1: Debug Receiver

1. Implement a TCP or WebSocket debug receiver for raw PCM.
2. Add a session model for connect, disconnect, and stream state.
3. Parse incoming audio frames into a stable internal buffer format.
4. Add basic stream stats: bytes received, packets/frames received, last activity time.
5. Add a simple local test mode using generated audio if a real sender is not connected.

Definition of done:

- The host can receive continuous raw PCM from a test sender.
- Stats update while streaming.
- The receiver can recover cleanly from disconnects.

### Phase 2: Local Audio Playback Spike

1. Enumerate Windows render devices.
2. Implement a debug output adapter that plays audio to a normal speaker/headphone device.
3. Add format conversion if the device format differs from the internal stream format.
4. Add underrun and overrun logging.
5. Measure approximate output latency and glitch rate.

Definition of done:

- Incoming stream audio plays through a normal Windows output device.
- Playback can run for several minutes without major glitches.
- Device selection and playback failures are diagnosable.

### Phase 3: VB-CABLE Integration

1. Detect whether VB-CABLE is installed.
2. Enumerate devices and identify the VB-CABLE render endpoint.
3. Replace the debug output adapter with a VB-CABLE output adapter.
4. Add startup checks and actionable error messages if VB-CABLE is missing or unusable.
5. Verify that the paired VB-CABLE recording endpoint appears in Windows apps as a microphone.

Definition of done:

- The host can write live stream audio into the VB-CABLE render endpoint.
- Discord, OBS, or a recorder can read the VB-CABLE recording endpoint as a mic.
- The app can tell the user what to select and how to fix common setup problems.

### Phase 4: Stream Robustness

1. Add a jitter buffer between receiver and output.
2. Add sequence numbers and timestamps to the host-side stream model.
3. Add packet/frame loss accounting.
4. Tune buffer targets for voice-chat latency instead of maximum stability.
5. Add silence insertion or fallback behavior for missing data.

Definition of done:

- Short network timing variation does not cause obvious breakup.
- The app surfaces jitter, drop, and underrun statistics.
- Voice latency remains within the MVP target budget.

### Phase 5: Real Transport Path

1. Replace the debug transport with the intended real-time transport.
2. Add session handshake and stream-start/stop control messages.
3. Add Opus decode support if the client moves off raw PCM.
4. Keep the output pipeline independent from transport details.

Definition of done:

- The host supports the intended MVP transport path.
- Audio output behavior remains stable after swapping transport layers.

### Phase 6: Host UX

1. Decide whether the first host is a simple window, tray app, or both.
2. Show connection status, selected output target, and VB-CABLE readiness.
3. Show the local IP and any manual connection details needed by the iOS client.
4. Add start/stop stream controls if useful for debugging.
5. Add a troubleshooting panel with device and stream diagnostics.

Definition of done:

- A user can tell whether the host is ready.
- The manual connect flow is obvious.
- The common failure cases are visible without reading logs.

### Phase 7: Install and Startup

1. Decide whether MVP requires the user to install VB-CABLE manually or whether the installer will guide/setup it.
2. Add startup checks for audio device availability and basic firewall/network readiness.
3. Add optional start-with-Windows support.
4. Add installer packaging for the host.

Definition of done:

- The host can be installed and launched on a clean Windows machine.
- Setup friction is documented and testable.

## Recommended Build Order

1. Phase 0: Host Foundation
2. Phase 1: Debug Receiver
3. Phase 2: Local Audio Playback Spike
4. Phase 3: VB-CABLE Integration
5. Phase 4: Stream Robustness
6. Phase 5: Real Transport Path
7. Phase 6: Host UX
8. Phase 7: Install and Startup

## First Sprint

These are the highest-value tasks to start immediately:

1. Create the `.NET 8` solution and project layout.
2. Define the host-side internal audio format.
3. Implement the raw PCM debug receiver.
4. Implement normal Windows playback to a selected render device.
5. Verify the same playback path can be redirected to VB-CABLE.

## Open Host Questions

1. Should the first host UI be a console app, a minimal desktop window, or a tray app?
2. Which audio library should back the MVP output adapter: direct WASAPI interop or `NAudio`?
3. What exact internal audio frame shape should the host standardize on for MVP?
4. What latency target defines success for the host spike?
