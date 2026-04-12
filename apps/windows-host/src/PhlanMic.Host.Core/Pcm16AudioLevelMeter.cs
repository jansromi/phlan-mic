using System.Buffers.Binary;

namespace PhlanMic.Host.Core;

public sealed class Pcm16AudioLevelMeter
{
    private const double MaxPcm16Amplitude = 32768d;
    private const double SignalThresholdNormalized = 0.01d;
    private const double DisplayDecayPerSecond = 1.25d;
    private static readonly TimeSpan DisplayHoldDuration = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan InstantaneousReadingLifetime = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan RecentSignalLifetime = TimeSpan.FromMilliseconds(900);

    private double peakNormalized;
    private double rmsNormalized;
    private double displayPeakNormalized;
    private int clippedSampleCount;
    private DateTimeOffset? lastSignalAtUtc;
    private DateTimeOffset? lastObservedAtUtc;
    private DateTimeOffset? displayHoldUntilUtc;
    private DateTimeOffset? lastDisplayUpdateUtc;

    public void ObserveFrame(AudioFormat format, ReadOnlySpan<byte> payload, DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(format);
        format.Validate();

        if (payload.Length != format.BytesPerFrame)
        {
            throw new ArgumentException(
                $"PCM16 level meter expected {format.BytesPerFrame} bytes but received {payload.Length}.",
                nameof(payload));
        }

        if (format.BitsPerSample is not 16)
        {
            throw new InvalidOperationException("PCM16 level metering only supports 16-bit signed PCM frames.");
        }

        ObserveSamples(format, payload, observedAtUtc);
    }

    public void ObserveSamples(AudioFormat format, ReadOnlySpan<byte> payload, DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(format);
        format.Validate();

        if (format.BitsPerSample is not 16)
        {
            throw new InvalidOperationException("PCM16 level metering only supports 16-bit signed PCM frames.");
        }

        var blockAlign = format.Channels * format.BytesPerSample;
        if (blockAlign <= 0 || payload.Length % blockAlign != 0)
        {
            throw new ArgumentException(
                $"PCM16 level meter expected payload aligned to {blockAlign} bytes but received {payload.Length}.",
                nameof(payload));
        }

        ApplyDisplayDecay(observedAtUtc);

        if (payload.Length == 0)
        {
            RecordInstantaneousLevels(0, 0, 0, observedAtUtc);
            return;
        }

        double sumSquares = 0;
        double peak = 0;
        var clippedSamples = 0;
        var sampleCount = payload.Length / sizeof(short);

        for (var offset = 0; offset < payload.Length; offset += sizeof(short))
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(offset, sizeof(short)));
            var absolute = sample == short.MinValue ? 32768 : Math.Abs(sample);
            if (absolute >= short.MaxValue)
            {
                clippedSamples++;
            }

            peak = Math.Max(peak, absolute / MaxPcm16Amplitude);
            sumSquares += (double)absolute * absolute;
        }

        var rms = sampleCount == 0
            ? 0
            : Math.Sqrt(sumSquares / sampleCount) / MaxPcm16Amplitude;

        RecordInstantaneousLevels(peak, rms, clippedSamples, observedAtUtc);
    }

    public AudioLevelMeterSnapshot GetSnapshot(DateTimeOffset observedAtUtc)
    {
        ApplyDisplayDecay(observedAtUtc);

        var instantaneousIsFresh = lastObservedAtUtc is not null &&
            observedAtUtc - lastObservedAtUtc.Value <= InstantaneousReadingLifetime;
        var recentSignalDetected = lastSignalAtUtc is not null &&
            observedAtUtc - lastSignalAtUtc.Value <= RecentSignalLifetime;

        return new AudioLevelMeterSnapshot(
            instantaneousIsFresh ? peakNormalized : 0,
            instantaneousIsFresh ? rmsNormalized : 0,
            displayPeakNormalized,
            instantaneousIsFresh ? clippedSampleCount : 0,
            recentSignalDetected || displayPeakNormalized >= SignalThresholdNormalized,
            lastSignalAtUtc,
            lastObservedAtUtc);
    }

    private void RecordInstantaneousLevels(double peak, double rms, int clippedSamples, DateTimeOffset observedAtUtc)
    {
        peakNormalized = peak;
        rmsNormalized = rms;
        clippedSampleCount = clippedSamples;
        lastObservedAtUtc = observedAtUtc;

        if (peak >= SignalThresholdNormalized || rms >= SignalThresholdNormalized)
        {
            lastSignalAtUtc = observedAtUtc;
        }

        if (peak > displayPeakNormalized)
        {
            displayPeakNormalized = peak;
            displayHoldUntilUtc = observedAtUtc + DisplayHoldDuration;
        }
        else if (peak > 0 && peak >= displayPeakNormalized - 0.01d)
        {
            displayPeakNormalized = Math.Max(displayPeakNormalized, peak);
            displayHoldUntilUtc = observedAtUtc + DisplayHoldDuration;
        }
    }

    private void ApplyDisplayDecay(DateTimeOffset observedAtUtc)
    {
        if (lastDisplayUpdateUtc is null)
        {
            lastDisplayUpdateUtc = observedAtUtc;
            return;
        }

        if (observedAtUtc <= lastDisplayUpdateUtc.Value || displayPeakNormalized <= 0)
        {
            lastDisplayUpdateUtc = observedAtUtc;
            return;
        }

        var effectiveDecayStart = lastDisplayUpdateUtc.Value;
        if (displayHoldUntilUtc is not null)
        {
            if (observedAtUtc <= displayHoldUntilUtc.Value)
            {
                lastDisplayUpdateUtc = observedAtUtc;
                return;
            }

            if (displayHoldUntilUtc.Value > effectiveDecayStart)
            {
                effectiveDecayStart = displayHoldUntilUtc.Value;
            }
        }

        var decayElapsed = observedAtUtc - effectiveDecayStart;
        if (decayElapsed > TimeSpan.Zero)
        {
            displayPeakNormalized = Math.Max(0, displayPeakNormalized - (decayElapsed.TotalSeconds * DisplayDecayPerSecond));
        }

        lastDisplayUpdateUtc = observedAtUtc;
    }
}
