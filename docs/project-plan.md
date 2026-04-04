# Project Plan

## Hard Truth Up Front

Streaming microphone audio from iOS to Windows is very feasible.

Making that audio appear as a normal Windows microphone endpoint is the hard part. A normal desktop app does not simply "become" a microphone device for the rest of the OS. In practice, this requires one of these approaches:

- Ship or depend on a virtual audio driver that exposes a microphone endpoint.
- Build and sign our own Windows virtual audio driver.

Because of that, the project should be split into an MVP and a later product-grade version.

## Recommended Product Shape

### MVP

- iOS app captures microphone audio and streams it to the PC.
- Windows server receives audio, decodes it, and outputs it into an existing virtual audio device.
- VB-CABLE is the chosen virtual audio device for the MVP path.
- The installer either:
  - bundles a signed third-party virtual audio device, or
  - requires the user to install one supported virtual audio device separately.

This gets us to a working product much faster and avoids turning phase 1 into a driver project.

### V2

- Replace the external dependency with our own signed virtual microphone driver.
- Bundle server, driver, and pairing flow into one polished install experience.

## Architecture

### 1. iOS/iPadOS Client

Responsibilities:

- Ask for microphone permission.
- Ask for local network permission if using Bonjour or direct LAN discovery.
- Capture mono microphone audio.
- Encode audio for transport.
- Send audio packets to the selected PC server with low latency.
- Show basic input meter, mute state, and connection quality.

Recommended stack:

- Swift
- AVAudioSession + AVAudioEngine for capture
- Network.framework for networking
- Bonjour for discovery

Audio capture defaults:

- 48 kHz
- Mono
- 20 ms packet cadence

### 2. Windows Server App

Responsibilities:

- Advertise itself on the local network for easy discovery.
- Accept pairing and streaming connections from the iOS client.
- Receive and decode audio in real time.
- Manage a jitter buffer and packet loss handling.
- Feed the decoded stream into the selected virtual audio endpoint.
- Expose a tiny desktop UI or tray UI for status, pairing, and troubleshooting.

Recommended stack:

- .NET 8 desktop app for the server UI and app lifecycle
- Native audio/network helper only if latency or driver integration demands it

Current recommendation:

- Use .NET 8 for the MVP unless a strong Rust-specific requirement appears during the spike.

Core modules:

- Discovery service
- Pairing/auth service
- Audio receiver
- Jitter buffer
- Decoder
- Audio output adapter
- Device health/diagnostics

### 3. Virtual Microphone Path

This is the part that decides the project scope.

Options:

1. Existing virtual audio device for MVP
2. Custom virtual driver for product-grade release

Recommended path:

Start with option 1.

If the chosen virtual audio device exposes a linked virtual speaker + mic pair, the server can play received audio into the virtual speaker side, and other apps can read it from the virtual microphone side. That is much simpler than writing a Windows audio driver first.

## Networking and Audio Transport

### Discovery

Recommended:

- Advertise the Windows server with Bonjour/mDNS on the LAN.
- Show discovered PCs automatically in the iOS app.
- Keep manual IP entry as a fallback.

### Pairing

Recommended:

- First-time pairing with a short code or QR code shown by the Windows app.
- Store a device token after approval.
- Allow one active streaming device at a time for MVP.

### Transport Protocol

Recommended MVP:

- UDP for audio packets
- Small reliable control channel for pairing and session setup

Audio codec recommendation:

- Opus

Why:

- Designed for low-latency voice
- Handles packet loss better than raw PCM
- Saves bandwidth versus uncompressed audio

Fallback debug mode:

- Raw PCM over TCP/WebSocket for initial bring-up only

That is useful early in development, but it should not be the long-term transport path.

For the first end-to-end prototype, manual server IP entry is acceptable and preferred over adding Bonjour immediately.

### Jitter Strategy

Start simple:

- 40 to 80 ms target jitter buffer
- Sequence numbers per packet
- Packet timestamps
- Packet loss concealment handled by decoder where possible

## UX Flow

### First Run

Windows:

1. Launch app
2. Install or validate virtual audio device
3. Show pairing code and local server name

iPhone/iPad:

1. Launch app
2. Request microphone permission
3. Request local network permission if needed
4. Discover server automatically or let user enter IP
5. Pair
6. Start streaming

For MVP, the app may remain in the foreground while streaming.

### Regular Use

1. Open Windows app or let it start with Windows
2. Open mobile app
3. Tap connect
4. Windows virtual microphone is available in apps

## Suggested Repository Layout

```text
phlan-mic/
  README.md
  docs/
    project-plan.md
  apps/
    ios/
    windows-server/
  shared/
    protocol/
  tools/
```

## Milestones

### Milestone 0: Spike and De-risk

Goal:

Prove the core technical path before building product polish.

Deliverables:

- Confirm the virtual audio strategy on Windows
- Stream audio from iOS simulator substitute or test client to Windows
- Feed received audio into a Windows output path
- Measure end-to-end latency budget

Exit criteria:

- We know whether MVP will use an external virtual audio device or whether this project must include driver work immediately

### Milestone 1: Desktop Receiver Prototype

Goal:

Have a PC app that can receive streamed audio and play it into a target device.

Deliverables:

- Simple desktop server
- Manual IP/session connect
- Raw PCM transport for initial debugging
- Basic audio statistics
- Debug playback to a normal Windows output device before VB-CABLE integration

Exit criteria:

- The Windows side can receive continuous test audio without obvious glitches

### Milestone 2: iOS Capture Prototype

Goal:

Send live microphone audio from a real iPhone/iPad to the PC.

Deliverables:

- AVAudioEngine capture
- Manual server address entry
- Start/stop streaming
- Audio level meter

Exit criteria:

- Live speech reaches the PC reliably on one LAN

### Milestone 3: Low-Latency Streaming

Goal:

Replace debugging transport with the real-time path.

Deliverables:

- Opus encode/decode
- UDP audio packets
- Jitter buffer
- Sequence numbers and timing

Exit criteria:

- Speech is clear and stable with acceptable latency for voice chat

### Milestone 4: Virtual Mic Integration

Goal:

Make Windows apps see the stream as a microphone.

Deliverables:

- Output adapter for VB-CABLE
- Device setup checks
- Basic troubleshooting UI

Exit criteria:

- Discord, OBS, or a sample recording app can use the streamed audio as a mic

### Milestone 5: Discovery and Pairing

Goal:

Make the system pleasant to use.

Deliverables:

- Bonjour discovery
- Pairing code or QR flow
- Trusted device list
- Reconnect flow

Exit criteria:

- A non-technical user can connect without typing IP addresses

### Milestone 6: Product Polish

Goal:

Make the app stable enough for daily use.

Deliverables:

- Auto reconnect
- Mute and push-to-talk options
- Start with Windows
- Network diagnostics
- Better error messages
- Installer packaging

## Biggest Risks

### 1. Virtual microphone strategy

This is the biggest project risk by far. If we insist on "no external virtual device dependency" from day one, the project expands into Windows driver development, driver signing, packaging, and support.

### 2. iOS background behavior

iOS is strict about background execution. For MVP, assume the app must stay in the foreground while streaming unless testing proves a supported background mode matches the use case.

### 3. Latency tuning

Audio that is technically working but delayed by 200 to 400 ms will feel bad in voice chat. The design should treat latency as a first-class requirement from the start.

### 4. Wi-Fi quality

Packet loss, roaming, and congested 2.4 GHz networks will affect quality. Diagnostics matter.

## Non-Goals for MVP

- Internet relay outside the local network
- Multiple simultaneous mobile senders
- Studio-grade audio processing
- Full echo cancellation on the PC side
- Seamless background streaming on iOS

## Recommended First Build Slice

Build the smallest vertical slice that proves the project is real:

1. Windows receiver app with a debug audio playback target
2. iOS app that captures mic audio and streams raw PCM to manual IP
3. Replace playback target with VB-CABLE
4. Only after that, add Opus, discovery, and pairing polish

This order keeps the hardest unknowns visible early.

## MVP Decisions

The following MVP decisions are now in place:

1. We accept an external virtual audio device for MVP and will use VB-CABLE first.
2. We will start with manual server IP entry and defer Bonjour discovery until after the core audio path is proven.
3. Foreground-only streaming on iOS is acceptable for MVP, but the app structure should remain easy to refactor if background behavior becomes a later requirement.

The remaining implementation decision to confirm is:

1. Should the Windows server MVP be implemented in .NET 8 or Rust?

Current recommendation:

- Use .NET 8 for the Windows server MVP because the main risk is virtual audio integration, not raw systems performance.
- Keep the code modular so a future native or Rust component can be introduced if performance or device integration later demands it.

## Notes From Platform Docs

- Microsoft provides the SysVAD sample as a virtual audio device driver sample, which reinforces that a virtual microphone path is fundamentally a driver-level problem rather than just a normal desktop app feature.
- Apple’s audio/session docs support live microphone capture through `AVAudioSession` and `AVAudioEngine`.
- Apple’s local network privacy guidance requires the app to declare and explain local network access, and Bonjour service types must be declared if Bonjour is used.

References:

- Microsoft SysVAD sample: https://learn.microsoft.com/en-us/samples/microsoft/windows-driver-samples/sysvad-virtual-audio-device-driver-sample/
- Microsoft audio driver samples: https://learn.microsoft.com/en-us/windows-hardware/drivers/samples/audio-driver-samples
- Apple live audio capture example: https://developer.apple.com/documentation/speech/recognizing-speech-in-live-audio
- Apple local network privacy: https://developer.apple.com/videos/play/wwdc2020/10110/

## Virtual Audio Device Options For MVP

The two most relevant candidates today are VB-CABLE and Virtual Audio Cable (VAC).

| Topic | VB-CABLE | VAC |
| --- | --- | --- |
| Basic routing model | Very simple loopback: audio sent to the playback side is forwarded to the recording side. Good fit for "server writes audio, apps read mic". | Same core loopback model, but more configurable. Each virtual cable exposes linked playback and recording endpoints. |
| Official wording | Vendor describes it as a virtual audio device where all audio coming in the input is forwarded to the output. | Vendor describes it as an audio bridge that creates virtual audio devices whose output is internally connected to input. |
| Windows support | Official site lists XP through Windows 11, plus Arm64 package support in Pack45 from October 2024. | Official site lists Windows XP through Windows 11. |
| Install friction | Admin install from extracted ZIP and vendor explicitly says to reboot after install/uninstall. | Admin install from EXE or unpacked ZIP. Vendor says restart is usually not required after successful install, though it can be required in some cases. |
| Mic endpoint naming | Conceptually simple: playback device feeds a recording device. Easier for users to understand. | More technical naming by default, such as `Line 1 (Virtual Audio Cable)`, with optional source lines including `Mic`, `Line`, and `S/PDIF`. |
| Endpoint count and flexibility | Best when we only need one or a few simple cables. Additional cable packs exist. | Much more flexible. The downloads page lists support up to 256 virtual cables in the full version. |
| Configurability | Lower. That is a strength for MVP because there is less to misconfigure. | Higher. Useful if we need custom formats, multiple cable topologies, or advanced troubleshooting. |
| Trial/evaluation path | Downloadable immediately from the official site. Donationware model, with professional and volume licensing through the webshop. | Official site offers a fully functional trial and a feature-limited Lite version. Full version is paid. |
| Distribution/OEM story | More straightforward for basic redistribution on paper. Vendor licensing page mentions professional use, server installation, volume licensing, and distribution/reselling terms through the webshop. | Better long-term developer story. Vendor explicitly offers custom proprietary builds for companion apps and even source code licensing by contact. |
| MVP fit for this project | Strongest choice if the goal is to prove the product quickly with the least routing complexity. | Strong choice if we expect to need more control, more cables, or a tighter eventual companion-app integration path. |

## Recommendation: VB-CABLE vs VAC

For the first working version, use VB-CABLE.

Reasons:

- The routing model is easier to explain and support.
- It appears to be the faster path to "make streamed audio show up as a recording device."
- The installation story is a bit blunt because of the reboot requirement, but the product concept is very easy to reason about.

Keep VAC as the backup or second evaluation path.

Reasons:

- It has a more explicit developer customization path.
- It exposes more advanced control if we later need multiple cables or more detailed tuning.
- It is probably the better fit if the project grows into a more configurable desktop audio tool instead of a narrow single-purpose mic bridge.

## What To Validate In A Short Spike

Before we commit to either one, the Windows spike should answer these questions:

1. Can our server write to the playback side reliably with low enough latency for voice chat?
2. Does Discord, OBS, and at least one game see the recording side as a normal microphone?
3. What is the real user friction of installation on a clean Windows 11 machine?
4. Are the vendor licensing terms acceptable for the way we want to distribute the app?

## Source Notes

- VB-CABLE product page: https://vb-audio.com/Cable/
- VB-CABLE reference manual: https://vb-audio.com/Cable/VBCABLE_ReferenceManual.pdf
- VB-Audio licensing page: https://vb-audio.com/Services/licensing.htm
- VB-CABLE webshop page: https://shop.vb-audio.com/en/win-apps/11-vb-cable.html
- VAC main page: https://vac.muzychenko.net/en/
- VAC install manual: https://vac.muzychenko.net/en/manual/install.htm
- VAC downloads page: https://vac.muzychenko.net/en/download.htm
- VAC purchase/custom versions page: https://vac.muzychenko.net/en/purchase.htm
