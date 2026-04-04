# phlan-mic

Turn an iPhone or iPad into a low-latency microphone for Windows apps on the same local network.

## Project Goal

The system has two parts:

- A Windows server app that receives audio from an iOS/iPadOS device and makes it usable as a normal microphone source on the PC.
- An iOS/iPadOS client app that captures device microphone audio and streams it to the Windows server in real time.

## Current Status

This repository is in planning mode. The first architecture and milestone draft lives in [docs/project-plan.md](docs/project-plan.md).
The Windows host task breakdown lives in [docs/windows-host-tasks.md](docs/windows-host-tasks.md).
