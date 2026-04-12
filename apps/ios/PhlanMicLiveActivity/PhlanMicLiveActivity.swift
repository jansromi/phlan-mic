import ActivityKit
import SwiftUI
import WidgetKit

struct PhlanMicLiveActivity: Widget {
    var body: some WidgetConfiguration {
        ActivityConfiguration(for: PhlanMicActivityAttributes.self) { context in
            LiveActivityLockScreenView(context: context)
                .activityBackgroundTint(LiveActivityPalette.panelBackgroundTop)
                .activitySystemActionForegroundColor(LiveActivityPalette.textPrimary)
        } dynamicIsland: { context in
            DynamicIsland {
                DynamicIslandExpandedRegion(.leading) {
                    ExpandedStatusView(state: context.state)
                }
                DynamicIslandExpandedRegion(.center) {
                    CenterMessageView(
                        message: context.state.alertReason ?? context.attributes.hostEndpoint,
                        emphasizesAlert: context.state.alertReason != nil
                    )
                }
                DynamicIslandExpandedRegion(.bottom) {
                    ExpandedMetricsView(state: context.state)
                }
            } compactLeading: {
                CompactLeadingView(state: context.state)
            } compactTrailing: {
                CompactTrailingView(state: context.state)
            } minimal: {
                StatusDot(tintName: context.state.transportStatusTint, diameter: 10)
            }
            .keylineTint(context.state.statusColor)
        }
    }
}

private struct LiveActivityLockScreenView: View {
    let context: ActivityViewContext<PhlanMicActivityAttributes>

    var body: some View {
        HStack(spacing: 14) {
            ZStack {
                Circle()
                    .fill(
                        LinearGradient(
                            colors: [LiveActivityPalette.statusBlue, LiveActivityPalette.statusGreen],
                            startPoint: .topLeading,
                            endPoint: .bottomTrailing
                        )
                    )

                Image(systemName: "mic.fill")
                    .font(.system(size: 18, weight: .semibold, design: .rounded))
                    .foregroundStyle(.white)
            }
            .frame(width: 42, height: 42)

            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 8) {
                    StatusDot(tintName: context.state.transportStatusTint, diameter: 10)
                    Text(verbatim: context.state.transportStatusLabel)
                        .font(.system(size: 16, weight: .bold, design: .rounded))
                        .foregroundStyle(LiveActivityPalette.textPrimary)
                        .lineLimit(1)
                }

                Text(verbatim: context.attributes.hostEndpoint)
                    .font(.system(size: 13, weight: .medium, design: .rounded))
                    .foregroundStyle(LiveActivityPalette.textSecondary)
                    .lineLimit(1)
            }

            Spacer(minLength: 12)

            VStack(alignment: .trailing, spacing: 6) {
                MetricPill(label: "Level", value: context.state.levelLabel)
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 14)
        .background(
            LinearGradient(
                colors: [LiveActivityPalette.panelBackgroundTop, LiveActivityPalette.panelBackgroundBottom],
                startPoint: .topLeading,
                endPoint: .bottomTrailing
            ),
            in: RoundedRectangle(cornerRadius: 22, style: .continuous)
        )
        .overlay {
            RoundedRectangle(cornerRadius: 22, style: .continuous)
                .stroke(LiveActivityPalette.tileBorder, lineWidth: 1)
        }
    }
}

private struct CompactLeadingView: View {
    let state: PhlanMicActivityAttributes.ContentState

    var body: some View {
        HStack(spacing: 4) {
            StatusDot(tintName: state.transportStatusTint, diameter: 8)
            Text(verbatim: state.shortStatusLabel)
                .font(.system(size: 12, weight: .semibold, design: .rounded))
                .foregroundStyle(LiveActivityPalette.islandTextPrimary)
                .lineLimit(1)
        }
    }
}

private struct CompactTrailingView: View {
    let state: PhlanMicActivityAttributes.ContentState

    var body: some View {
        Text(verbatim: state.isStreaming ? state.levelLabel : state.shortStatusLabel)
            .font(.system(size: 12, weight: .bold, design: .rounded))
            .foregroundStyle(LiveActivityPalette.islandTextPrimary)
            .monospacedDigit()
            .lineLimit(1)
    }
}

private struct ExpandedStatusView: View {
    let state: PhlanMicActivityAttributes.ContentState

    var body: some View {
        HStack(spacing: 8) {
            StatusDot(tintName: state.transportStatusTint, diameter: 10)

            Text(verbatim: state.transportStatusLabel)
                .font(.system(size: 15, weight: .bold, design: .rounded))
                .foregroundStyle(LiveActivityPalette.islandTextPrimary)
                .lineLimit(1)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

private struct ExpandedMetricsView: View {
    let state: PhlanMicActivityAttributes.ContentState

    var body: some View {
        HStack(spacing: 12) {
            LiveLevelMeter(levelPercentage: state.audioLevelPercentage)
                .frame(maxWidth: .infinity)

            HStack(spacing: 10) {
                Text(verbatim: state.levelLabel)
                    .font(.system(size: 13, weight: .bold, design: .rounded))
                    .foregroundStyle(state.statusColor)
                    .monospacedDigit()
            }
        }
    }
}

private struct CenterMessageView: View {
    let message: String
    let emphasizesAlert: Bool

    var body: some View {
        Text(verbatim: message)
            .font(.system(size: 12, weight: emphasizesAlert ? .semibold : .medium, design: .rounded))
            .foregroundStyle(
                emphasizesAlert
                    ? LiveActivityPalette.islandTextPrimary
                    : LiveActivityPalette.islandTextSecondary
            )
            .lineLimit(2)
            .multilineTextAlignment(.center)
            .frame(maxWidth: .infinity)
    }
}

private struct TransportModeBadge: View {
    let text: String

    var body: some View {
        Text(verbatim: text)
            .font(.system(size: 11, weight: .semibold, design: .rounded))
            .foregroundStyle(LiveActivityPalette.islandTextPrimary)
            .padding(.horizontal, 10)
            .padding(.vertical, 5)
            .background(
                Capsule()
                    .fill(LiveActivityPalette.islandChromeFill)
            )
            .overlay {
                Capsule()
                    .stroke(LiveActivityPalette.islandChromeStroke, lineWidth: 1)
            }
            .fixedSize(horizontal: true, vertical: true)
    }
}

private struct MetricPill: View {
    let label: String
    let value: String

    var body: some View {
        HStack(spacing: 6) {
            Text(verbatim: label)
                .font(.system(size: 11, weight: .semibold, design: .rounded))
                .foregroundStyle(LiveActivityPalette.textMuted)

            Text(verbatim: value)
                .font(.system(size: 12, weight: .bold, design: .rounded))
                .foregroundStyle(LiveActivityPalette.textPrimary)
                .monospacedDigit()
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 6)
        .background(
            Capsule()
                .fill(LiveActivityPalette.meterTrack)
        )
    }
}

private struct LiveLevelMeter: View {
    let levelPercentage: Int

    var body: some View {
        GeometryReader { proxy in
            let width = max(proxy.size.width * CGFloat(levelPercentage) / 100, levelPercentage > 0 ? 10 : 0)

            ZStack(alignment: .leading) {
                Capsule()
                    .fill(LiveActivityPalette.islandMeterTrack)

                Capsule()
                    .fill(
                        LinearGradient(
                            colors: [LiveActivityPalette.meterLow, LiveActivityPalette.meterMid, LiveActivityPalette.statusGreen],
                            startPoint: .leading,
                            endPoint: .trailing
                        )
                    )
                    .frame(width: width)
            }
        }
        .frame(height: 8)
    }
}

private struct StatusDot: View {
    let tintName: String
    let diameter: CGFloat

    var body: some View {
        Circle()
            .fill(LiveActivityPalette.color(named: tintName))
            .frame(width: diameter, height: diameter)
    }
}

private extension PhlanMicActivityAttributes.ContentState {
    var statusColor: Color {
        LiveActivityPalette.color(named: transportStatusTint)
    }

    var isStreaming: Bool {
        shortStatusLabel == "Live"
    }

    var levelLabel: String {
        "\(audioLevelPercentage)%"
    }

    var framesLabel: String {
        framesSent.formatted(.number.grouping(.automatic))
    }
}

private enum LiveActivityPalette {
    static let panelBackgroundTop = Color(.sRGB, red: 0.985, green: 0.99, blue: 0.995)
    static let panelBackgroundBottom = Color(.sRGB, red: 0.90, green: 0.93, blue: 0.97)
    static let tileBorder = slate.opacity(0.10)
    static let meterTrack = Color(.sRGB, red: 0.12, green: 0.18, blue: 0.26).opacity(0.10)
    static let islandTextPrimary = Color.white
    static let islandTextSecondary = Color.white.opacity(0.78)
    static let islandChromeFill = Color.white.opacity(0.14)
    static let islandChromeStroke = Color.white.opacity(0.16)
    static let islandMeterTrack = Color.white.opacity(0.18)
    static let textPrimary = Color(.sRGB, red: 0.12, green: 0.17, blue: 0.24)
    static let textSecondary = Color(.sRGB, red: 0.29, green: 0.36, blue: 0.45)
    static let textMuted = Color(.sRGB, red: 0.42, green: 0.48, blue: 0.57)
    static let meterLow = Color(.sRGB, red: 0.16, green: 0.55, blue: 0.76)
    static let meterMid = Color(.sRGB, red: 0.29, green: 0.70, blue: 0.40)
    static let statusOrange = Color(.sRGB, red: 0.96, green: 0.52, blue: 0.12)
    static let statusBlue = Color(.sRGB, red: 0.11, green: 0.45, blue: 0.87)
    static let statusYellow = Color(.sRGB, red: 0.95, green: 0.76, blue: 0.06)
    static let statusGreen = Color(.sRGB, red: 0.18, green: 0.68, blue: 0.38)
    static let statusRed = Color(.sRGB, red: 0.86, green: 0.20, blue: 0.18)
    static let slate = Color(.sRGB, red: 0.25, green: 0.35, blue: 0.43)

    static func color(named tintName: String) -> Color {
        switch tintName {
        case "orange":
            statusOrange
        case "blue":
            statusBlue
        case "yellow":
            statusYellow
        case "green":
            statusGreen
        case "red":
            statusRed
        default:
            slate
        }
    }
}
