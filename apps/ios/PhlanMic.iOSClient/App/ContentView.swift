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
                    .disabled(model.microphonePermission == .simulatorUnavailable)
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
                .foregroundStyle(Color(tintName))
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
