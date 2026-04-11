# Project Status Snapshot

Date: 2026-04-11

This document is a repo-state recap based on:

- the current `dev` branch
- the latest committed work on `dev`
- implementation notes under `.codex/`
- the currently checked-in runtime/config files

It is intentionally a status snapshot, not a forward-looking plan.

## Executive Summary

The project is no longer in a pure planning phase, even though [README.md](../README.md) still says so.

The repo now contains a working MVP-style prototype with:

- a Windows host runtime
- a Windows desktop UI
- local playback and VB-CABLE output support
- a debug TCP input path
- a realtime `UdpRawPcm` host path
- an iOS client with live microphone capture
- iOS debug TCP streaming
- iOS realtime UDP streaming with a TCP control channel
- iOS lifecycle hardening for scene changes and audio-session events

The current state is best described as:

- end-to-end prototype works
- core audio/transport pieces exist on both platforms
- host diagnostics and operator UX are substantially ahead of product polish
- packaging, discovery, pairing, background policy, and install experience are still unfinished

## Branch State

Current observed branch state:

- branch: `dev`
- remote tracking: `origin/dev`
- working tree: clean
- latest commit: `4a204a7`
- latest commit date: 2026-04-05 23:08:30 +0300

Latest commits on `dev` show that the most recent work was concentrated on 2026-04-05 and focused on:

- Windows host UI layout polish
- iOS session health and connection-state UI polish
- iOS lifecycle handling
- iOS startup styling cleanup
- audio activity and meter improvements

## What The Repo Clearly Has Today

### Windows Host

The Windows side has progressed through the originally planned host phases much further than the top-level docs imply.

Implemented foundation:

- `.NET 8` solution and host/core split
- config-driven startup
- structured logging
- internal fixed MVP audio format

Implemented streaming/output path:

- debug raw PCM receiver over TCP
- Windows local playback path
- VB-CABLE endpoint discovery and output sink
- jitter buffer and stream robustness behavior
- stream/session diagnostics and counters
- generated test source and sender-side validation tooling

Implemented operator UX:

- reusable `WindowsHostRuntime`
- `WindowsHostRuntimeSnapshot`
- WinForms shell in `PhlanMic.WindowsHost.Ui`
- readiness, manual connect, output status, session state, counters, and fault display
- console-attached logging for interactive Windows testing

Important repo evidence:

- [.codex/phase0-worknotes.txt](../.codex/phase0-worknotes.txt)
- [.codex/phase1-worknotes.txt](../.codex/phase1-worknotes.txt)
- [.codex/phase2-worknotes.txt](../.codex/phase2-worknotes.txt)
- [.codex/phase3-worknotes.txt](../.codex/phase3-worknotes.txt)
- [.codex/phase4-worknotes.txt](../.codex/phase4-worknotes.txt)
- [.codex/phase6-worknotes.txt](../.codex/phase6-worknotes.txt)

Code-level evidence of later transport work:

- `apps/windows-host/src/PhlanMic.Host.Core/UdpRawPcmReceiver.cs`
- `apps/windows-host/src/PhlanMic.WindowsHost/WindowsHostRuntime.cs`
- `apps/windows-host/src/PhlanMic.WindowsHost/VbCablePlaybackSink.cs`

### iOS Client

The iOS app has also moved well beyond the early task docs.

Implemented client foundation:

- SwiftUI app shell
- microphone permission flow
- manual host configuration
- microphone capture using `AVAudioSession` and `AVAudioEngine`
- framed mono PCM output aligned to the host MVP format
- input meter and debug diagnostics

Implemented transport paths:

- debug TCP sender path
- realtime `UdpRawPcm` transport
- TCP control + UDP audio split
- protocol serialization support
- transport state model and session counters

Implemented later hardening/polish:

- scene phase handling
- capture interruption and route-change handling
- richer session health and connection-state UI
- startup UI simplification and cleanup

Important repo evidence:

- [.codex/phase1-ios-worknotes.txt](../.codex/phase1-ios-worknotes.txt)
- [.codex/phase2-ios-worknotes.txt](../.codex/phase2-ios-worknotes.txt)
- [.codex/2026-04-05-ios-lifecycle-hardening-implementation-plan.md](../.codex/2026-04-05-ios-lifecycle-hardening-implementation-plan.md)
- [.codex/issues/2026-04-05-ios-udp-choppy-audio-writeup.md](../.codex/issues/2026-04-05-ios-udp-choppy-audio-writeup.md)

Git history strongly indicates iOS moved beyond the older planning docs on 2026-04-05:

- `d0e0837` implemented `UdpRawPcmTransportClient`
- `ff48bbc` implemented scene state management and capture session event handling
- `3301600` improved app model and status UI
- `7ea52d7` updated session health and connection-state display

## Status By Original Phase Model

This is the most defensible read of the current repo state.

### Windows Host

Effectively landed:

- Phase 0: Host Foundation
- Phase 1: Debug Receiver
- Phase 2: Local Audio Playback
- Phase 3: VB-CABLE Integration
- Phase 4: Stream Robustness
- Phase 5: Realtime Transport Path
- Phase 6: Host UX

Not yet product-finished:

- Phase 7: Install and Startup

Notes:

- Phase 5 is present in code and commit history, but it is less fully journaled in `.codex` than the other Windows phases.
- The runtime can select `UdpRawPcm`, so this is more than an unrealized plan.

### iOS Client

Effectively landed:

- microphone capture prototype
- debug TCP streaming path
- realtime `UdpRawPcm` client
- diagnostics/session health work
- lifecycle hardening for interruption and scene transitions

Still clearly incomplete or unresolved:

- discovery and pairing
- polished end-user UX
- productized reconnect policy
- background continuation strategy
- packaging/distribution concerns

Notes:

- the iOS task document in [docs/ios-client-tasks.md](./ios-client-tasks.md) is stale relative to the codebase
- the repo has clearly advanced beyond the point where `udpRealtime` was only a placeholder

## Important Mismatches Between Docs And Reality

These mismatches matter because they change how the repo should be read.

### README Is Outdated

[README.md](../README.md) still says:

- "This repository is in planning mode."

That is no longer accurate.

The repo contains real implementations and later-stage polish work on both platforms.

### The Checked-In Windows Defaults Still Point At Older Bring-Up Modes

The default Windows host config in [apps/windows-host/src/PhlanMic.WindowsHost/appsettings.json](../apps/windows-host/src/PhlanMic.WindowsHost/appsettings.json) currently sets:

- `receiver.transportMode = DebugTcpRawPcm`
- `output.mode = WaveOut`

That means the checked-in default runtime path is still the older debug/local-output path, even though the codebase supports:

- `UdpRawPcm`
- `VbCable`

This is not necessarily wrong for local bring-up, but it does mean:

- repo defaults do not represent the most advanced supported path
- a fresh run from committed defaults will not exercise the full intended MVP pipeline

### Some Planning Docs Are Behind The Code

Both platform task docs still read more like pre-implementation planning than a current-state summary:

- [docs/windows-host-tasks.md](./windows-host-tasks.md)
- [docs/ios-client-tasks.md](./ios-client-tasks.md)

They are still useful as phase framing, but they are no longer a good source of truth for what is already built.

## What The Latest Work Focused On

The newest commits on `dev` suggest the engineering focus shifted from raw bring-up to hardening and polish.

Most recent focus areas:

- Windows host UI layout and responsiveness
- audio activity visualization on Windows
- iOS session health messaging
- iOS connection-state presentation
- iOS lifecycle behavior under scene changes and session events
- iOS startup UI cleanup

This is consistent with a project that already has the core vertical slice working and is trying to make it more operable.

## Current Risks And Gaps

The repo is functional, but not yet product-ready.

Main gaps still visible from code and notes:

- no polished install/package path for Windows host
- no tray behavior or startup integration yet
- no final discovery or pairing flow
- no polished first-run UX
- background continuation on iOS is still an explicit risk area
- config defaults do not yet reflect the most advanced supported path
- some documentation is stale enough to understate current capability

The most important product-level unknown remains:

- how much iOS background behavior is realistically supportable for this app shape without drifting into a different Apple platform category

## Bottom-Line Assessment

This repository currently represents a serious prototype/MVP integration effort, not a plan-only codebase.

The strongest accurate summary is:

- Windows host core is substantially built
- Windows host desktop UI exists and has been stabilized through real testing
- iOS realtime transport exists
- end-to-end audio streaming has been exercised
- current work has shifted toward lifecycle, diagnostics, and usability polish
- the remaining work is mostly productization, workflow cleanup, and policy decisions rather than basic feasibility

## Source Files Used For This Snapshot

Primary local sources used when writing this status recap:

- [README.md](../README.md)
- [docs/project-plan.md](./project-plan.md)
- [docs/windows-host-tasks.md](./windows-host-tasks.md)
- [docs/ios-client-tasks.md](./ios-client-tasks.md)
- [.codex/phase0-worknotes.txt](../.codex/phase0-worknotes.txt)
- [.codex/phase1-worknotes.txt](../.codex/phase1-worknotes.txt)
- [.codex/phase2-worknotes.txt](../.codex/phase2-worknotes.txt)
- [.codex/phase3-worknotes.txt](../.codex/phase3-worknotes.txt)
- [.codex/phase4-worknotes.txt](../.codex/phase4-worknotes.txt)
- [.codex/phase6-worknotes.txt](../.codex/phase6-worknotes.txt)
- [.codex/phase1-ios-worknotes.txt](../.codex/phase1-ios-worknotes.txt)
- [.codex/phase2-ios-worknotes.txt](../.codex/phase2-ios-worknotes.txt)
- [.codex/2026-04-05-ios-lifecycle-hardening-implementation-plan.md](../.codex/2026-04-05-ios-lifecycle-hardening-implementation-plan.md)
- [.codex/issues/2026-04-05-ios-udp-choppy-audio-writeup.md](../.codex/issues/2026-04-05-ios-udp-choppy-audio-writeup.md)
- [apps/windows-host/src/PhlanMic.WindowsHost/appsettings.json](../apps/windows-host/src/PhlanMic.WindowsHost/appsettings.json)

Git history used for the time/status read:

- latest commits on `dev` up to and including `4a204a7`
