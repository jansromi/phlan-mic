import SwiftUI

struct ContentView: View {
    @ObservedObject var model: AppModel

    var body: some View {
        NavigationStack {
            Form {
                Section("Foundation") {
                    LabeledStatusRow(
                        title: "Connection",
                        value: model.connectionStatus.rawValue,
                        tintName: model.connectionStatus.tintName
                    )
                    Text(model.connectionDetail)
                        .font(.footnote)
                        .foregroundStyle(.secondary)

                    LabeledStatusRow(
                        title: "Environment",
                        value: model.startupSnapshot.platformDescription,
                        tintName: model.startupSnapshot.isSimulator ? "orange" : "green"
                    )
                }

                Section("Microphone") {
                    LabeledStatusRow(
                        title: "Permission",
                        value: model.microphonePermission.label,
                        tintName: model.microphonePermission.tintName
                    )

                    Text(model.microphonePermission.detail)
                        .font(.footnote)
                        .foregroundStyle(.secondary)

                    Button("Request Permission") {
                        Task {
                            await model.requestMicrophonePermission()
                        }
                    }
                    .disabled(!model.canRequestMicrophonePermission)
                }

                Section("Capture") {
                    LabeledStatusRow(
                        title: "Status",
                        value: model.captureStatus.rawValue,
                        tintName: model.captureStatus.tintName
                    )

                    Text(model.captureDetail)
                        .font(.footnote)
                        .foregroundStyle(.secondary)

                    Button("Start Capture") {
                        Task {
                            await model.startCapture()
                        }
                    }
                    .disabled(!model.canStartCapture)

                    Button("Stop Capture") {
                        model.stopCapture()
                    }
                    .disabled(!model.canStopCapture)

                    KeyValueRow(label: "Session", value: model.captureSessionSummary)
                    KeyValueRow(label: "Framed Packets", value: "\(model.capturedFrameCount)")
                    KeyValueRow(label: "Latest Frame", value: model.latestFrameSummary)
                }

                Section("Input Meter") {
                    MeterRow(
                        label: "Average",
                        value: model.latestInputLevel.averageLevel,
                        detail: "\(model.latestInputLevel.averagePercentage)% (\(model.latestInputLevel.averageDecibels) dBFS)"
                    )
                    MeterRow(
                        label: "Peak",
                        value: model.latestInputLevel.peakLevel,
                        detail: "\(model.latestInputLevel.peakPercentage)% (\(model.latestInputLevel.peakDecibels) dBFS)"
                    )
                }

                Section("Host") {
                    TextField("Host address", text: hostAddressBinding)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()

                    TextField("Port", text: portTextBinding)
                        .keyboardType(.numberPad)

                    Picker("Transport", selection: transportModeBinding) {
                        ForEach(HostConfiguration.TransportMode.allCases) { mode in
                            Text(mode.label).tag(mode)
                        }
                    }

                    Button("Record Bring-Up Checkpoint") {
                        model.recordBringUpCheckpoint()
                    }
                }

                Section("Audio MVP") {
                    KeyValueRow(label: "Format", value: MVPAudioFormat.defaultVoice.debugSummary)
                    KeyValueRow(label: "Packet Model", value: MVPAudioPacket.prototype.debugSummary)
                }

                Section("Diagnostics") {
                    if model.diagnostics.isEmpty {
                        Text("No diagnostics yet.")
                            .foregroundStyle(.secondary)
                    } else {
                        ForEach(model.diagnostics) { entry in
                            VStack(alignment: .leading, spacing: 4) {
                                Text(entry.timestamp.formatted(date: .omitted, time: .standard))
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                                Text(entry.message)
                                    .font(.footnote)
                            }
                            .padding(.vertical, 2)
                        }
                    }
                }
            }
            .navigationTitle("PhlanMic")
        }
        .task {
            await model.loadStartupState()
        }
    }

    private var hostAddressBinding: Binding<String> {
        Binding(
            get: { model.hostConfiguration.hostAddress },
            set: { model.updateHostAddress($0) }
        )
    }

    private var portTextBinding: Binding<String> {
        Binding(
            get: { model.hostConfiguration.portText },
            set: { model.updatePortText($0) }
        )
    }

    private var transportModeBinding: Binding<HostConfiguration.TransportMode> {
        Binding(
            get: { model.hostConfiguration.transportMode },
            set: { model.updateTransportMode($0) }
        )
    }
}

private struct LabeledStatusRow: View {
    let title: String
    let value: String
    let tintName: String

    var body: some View {
        HStack {
            Text(title)
            Spacer()
            Text(value)
                .fontWeight(.semibold)
                .foregroundStyle(StatusTint.color(named: tintName))
        }
    }
}

private struct KeyValueRow: View {
    let label: String
    let value: String

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(label)
                .font(.subheadline.weight(.semibold))
            Text(value)
                .font(.footnote)
                .foregroundStyle(.secondary)
        }
        .padding(.vertical, 2)
    }
}

private struct MeterRow: View {
    let label: String
    let value: Float
    let detail: String

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text(label)
                    .font(.subheadline.weight(.semibold))
                Spacer()
                Text(detail)
                    .font(.footnote)
                    .foregroundStyle(.secondary)
            }

            ProgressView(value: Double(max(0, min(value, 1))), total: 1)
                .tint(value > 0.8 ? .red : (value > 0.4 ? .orange : .green))
        }
        .padding(.vertical, 2)
    }
}

private enum StatusTint {
    static func color(named tintName: String) -> Color {
        switch tintName {
        case "orange":
            .orange
        case "blue":
            .blue
        case "yellow":
            .yellow
        case "green":
            .green
        case "red":
            .red
        default:
            .primary
        }
    }
}
