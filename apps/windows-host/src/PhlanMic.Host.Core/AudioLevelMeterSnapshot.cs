namespace PhlanMic.Host.Core;

public sealed record AudioLevelMeterSnapshot(
    double PeakNormalized,
    double RmsNormalized,
    double DisplayPeakNormalized,
    int ClippedSampleCount,
    bool SignalDetected,
    DateTimeOffset? LastSignalAtUtc,
    DateTimeOffset? UpdatedAtUtc)
{
    public static AudioLevelMeterSnapshot Empty { get; } = new(
        0,
        0,
        0,
        0,
        false,
        null,
        null);
}
