# iOS Client Tasks

This document tracks the iPhone client work needed to catch up with the current Windows host implementation.

The Windows side now supports two input paths:

- `DebugTcpRawPcm`
- `UdpRawPcm` with a TCP control channel and UDP audio packets

The iOS client already has a usable foundation for manual host entry, microphone capture, status UI, and debug TCP streaming. The main gap is that the app still treats `udpRealtime` as a placeholder instead of implementing the same Phase 5 transport contract that now exists on Windows.

## Goal

Build an iPhone app that:

- captures microphone audio in the MVP format
- streams it to the Windows host over the real Phase 5 transport path
- surfaces enough state to debug capture, network, and host-side failures

## Current Status

What already exists in the iOS client:

- SwiftUI app shell with connection and session state
- microphone permission flow
- live microphone capture on device
- framed mono PCM output aligned with the MVP host format
- manual host address and port entry
- debug TCP transport client
- basic transport/capture diagnostics

What is still missing for parity with the Windows host:

- a transport-agnostic networking layer
- Phase 5 control protocol support on iOS
- `UdpRawPcm` transport implementation
- keepalive / timeout / reconnect handling for the real transport
- protocol-level diagnostics and tests

## MVP Alignment

Current shared MVP audio assumptions:

- `48 kHz`
- `mono`
- `16-bit signed PCM`
- `20 ms` packet cadence

Current intended real transport shape:

- reliable TCP control channel
- UDP audio packets
- explicit `hello` / `startStream` / `stopStream` / `keepAlive` messages
- versioned packet envelope

Near-term iOS objective:

- implement `UdpRawPcm` first
- keep payload format as raw PCM for now
- match the existing Windows host protocol exactly

This avoids introducing transport bugs and codec bugs at the same time.

## Codebase Reality

These points should drive implementation planning:

- `HostConfiguration.TransportMode` already exposes `tcpDebug` and `udpRealtime`.
- `AppModel` still hard-blocks `udpRealtime` as “not implemented yet”.
- the only concrete transport client is `DebugTcpPcmClient`
- capture is already producing stable framed PCM data suitable for the current host
- current app tests are written against a TCP-specific transport dependency surface

That means the next work is not “add audio capture”. It is “replace the TCP-only transport seam with a real transport seam and then implement the Phase 5 client”.

## Revised Task Breakdown

### Phase 0: Foundation Cleanup

1. Replace the TCP-specific transport dependency seam in `AppModel` with a transport-agnostic client interface.
2. Replace TCP-specific transport events with generic connection/session events.
3. Keep `DebugTcpRawPcm` as a supported bring-up path during the transition.
4. Preserve the current UI behavior while the internals change.

Definition of done:

- `AppModel` no longer depends on a TCP-specific client type.
- Transport mode selection is a real implementation choice, not a placeholder UI toggle.
- Existing TCP debug behavior still works.

### Phase 1: Shared Client Protocol Model

1. Add Swift models for the Phase 5 control messages.
2. Add Swift models for the UDP audio packet envelope.
3. Add serialization and deserialization helpers.
4. Match the Windows host protocol version and required fields exactly.
5. Add unit tests for encode/decode and malformed-message rejection.

Definition of done:

- The iOS client can construct valid Phase 5 control messages and audio packets.
- Protocol tests cover round-trip serialization and common invalid cases.
- The iOS contract stays aligned with the Windows host implementation.

### Phase 2: `UdpRawPcm` Transport Client

1. Implement a TCP control client for `hello`, `startStream`, `keepAlive`, and `stopStream`.
2. Implement a UDP sender for framed PCM packets.
3. Use the host-provided negotiated audio port from `helloAccepted`.
4. Add keepalive and session-timeout handling.
5. Make disconnect and reconnect behavior explicit instead of implicit.

Definition of done:

- The iOS client can complete a full Phase 5 session handshake with the Windows host.
- Live microphone frames can be sent over UDP after `startAccepted`.
- The client detects and reports host rejection, disconnects, and timeouts clearly.

### Phase 3: AppModel and UI Integration

1. Make `connectAndStream()` branch on the selected transport mode.
2. Remove the current `udpRealtime` “not implemented yet” block.
3. Update transport status messages so they reflect:
   - connecting control channel
   - handshake accepted
   - waiting for stream start
   - streaming
   - stopping
   - transport error
4. Keep the primary interaction simple: tap to start, tap to stop.
5. Keep manual host entry as the default flow.

Definition of done:

- Selecting `UDP Realtime` in the UI actually uses the real transport path.
- The app can stream to the Windows Phase 5 host without code changes.
- The user can tell whether failure happened before connect, during handshake, or during streaming.

### Phase 4: Diagnostics and Session Health

1. Add iOS-side counters for:
   - control messages sent
   - control messages received
   - frames sent
   - bytes sent
   - reconnect count
   - last successful send
   - last keepalive
   - last transport error
2. Surface these counters in the debug or diagnostics view first.
3. Distinguish capture failures from transport failures in UI copy.
4. Log state transitions in a way that can be compared against Windows host logs.

Definition of done:

- The app exposes enough state to debug normal LAN failures without Xcode attached.
- iOS diagnostics can be correlated with Windows host structured logs.

### Phase 5: Reconnect and Stall Resilience

1. Add reconnect behavior after host disconnect or short LAN interruption.
2. Decide whether reconnect should be automatic, manual, or bounded automatic retry for MVP.
3. Preserve sequence-number and timestamp continuity rules deliberately.
4. Avoid flooding the host with stale reconnect attempts.
5. Keep capture lifecycle predictable while transport state changes.

Definition of done:

- Short transport failures do not leave the app in a confused half-connected state.
- Reconnect behavior is predictable and diagnosable.

### Phase 6: Background, Interruptions, and Route Changes

1. Handle interruptions such as phone calls or route changes cleanly.
2. Stop or pause transport explicitly when capture becomes invalid.
3. Resume only when the app can do so predictably.
4. Keep user-facing state clear during interruptions.

Definition of done:

- The host is not left waiting on a dead stream when iOS interrupts capture.
- The user can tell why streaming paused or stopped.

### Phase 7: Future Payload Work

1. Introduce a payload decoder/encoder seam on iOS before adding Opus.
2. Keep raw PCM as the first production transport payload until the real transport is stable.
3. Only after transport stability is proven, evaluate Opus encode support.

Definition of done:

- The client is ready to evolve toward Opus without another transport rewrite.

## Recommended Build Order

1. Phase 0: Foundation Cleanup
2. Phase 1: Shared Client Protocol Model
3. Phase 2: `UdpRawPcm` Transport Client
4. Phase 3: AppModel and UI Integration
5. Phase 4: Diagnostics and Session Health
6. Phase 5: Reconnect and Stall Resilience
7. Phase 6: Background, Interruptions, and Route Changes
8. Phase 7: Future Payload Work

## Immediate Catch-Up Plan

These are the highest-value tasks to do next:

1. Refactor `AppModel` to depend on a transport-agnostic client interface.
2. Port the current Windows Phase 5 protocol contract into Swift types and tests.
3. Implement the `UdpRawPcm` client using Network.framework.
4. Wire `udpRealtime` into the existing UI and status model.
5. Verify end-to-end on a real iPhone against the Windows host in `UdpRawPcm` mode.

## Validation Targets

Minimum validation for “iOS is up to speed”:

1. The app can stream live microphone audio from a real iPhone to the Windows host over `UdpRawPcm`.
2. The Windows host reports:
   - accepted frames increasing
   - zero protocol errors
   - zero decode failures
   - zero unexpected packet rejections in baseline runs
3. The iOS app can stop and restart streaming without force-quitting.
4. The iOS app can distinguish:
   - microphone permission/configuration failure
   - control-channel failure
   - UDP streaming failure
   - host rejection

## Open Questions

1. Should the iOS MVP keep both `DebugTcpRawPcm` and `UdpRawPcm`, or should debug TCP become a hidden/dev-only mode once realtime transport is stable?
2. Do we want bounded automatic reconnect for MVP, or explicit manual reconnect only?
3. Should host discovery remain manual-IP-first until the realtime transport is stable?
4. When should Opus enter the plan: immediately after transport stabilization, or only after the iOS realtime path has been used for sustained testing?
