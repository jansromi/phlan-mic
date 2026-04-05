using System.Buffers.Binary;
using PhlanMic.Host.Core;

namespace PhlanMic.Host.Core.Tests;

public sealed class Pcm16AudioLevelMeterTests
{
    private static readonly AudioFormat TestFormat = new()
    {
        SampleRate = 400,
        Channels = 1,
        BitsPerSample = 16,
        FrameDurationMs = 10
    };

    [Fact]
    public void ObserveFrameCapturesPeakRmsAndClipping()
    {
        var meter = new Pcm16AudioLevelMeter();
        var observedAtUtc = new DateTimeOffset(2026, 4, 5, 12, 0, 0, TimeSpan.Zero);

        meter.ObserveFrame(TestFormat, CreatePayload(0, 16384, -16384, short.MaxValue), observedAtUtc);
        var snapshot = meter.GetSnapshot(observedAtUtc);

        Assert.True(snapshot.SignalDetected);
        Assert.InRange(snapshot.PeakNormalized, 0.99d, 1.0d);
        Assert.InRange(snapshot.RmsNormalized, 0.60d, 0.65d);
        Assert.Equal(1, snapshot.ClippedSampleCount);
        Assert.Equal(observedAtUtc, snapshot.LastSignalAtUtc);
    }

    [Fact]
    public void GetSnapshotDropsInstantaneousLevelsWhenReadingIsStale()
    {
        var meter = new Pcm16AudioLevelMeter();
        var observedAtUtc = new DateTimeOffset(2026, 4, 5, 12, 0, 0, TimeSpan.Zero);

        meter.ObserveFrame(TestFormat, CreatePayload(2000, -2000, 2000, -2000), observedAtUtc);
        var staleSnapshot = meter.GetSnapshot(observedAtUtc.AddMilliseconds(250));

        Assert.Equal(0, staleSnapshot.PeakNormalized);
        Assert.Equal(0, staleSnapshot.RmsNormalized);
        Assert.Equal(0, staleSnapshot.ClippedSampleCount);
        Assert.True(staleSnapshot.DisplayPeakNormalized > 0);
    }

    [Fact]
    public void GetSnapshotHoldsAndThenDecaysDisplayPeak()
    {
        var meter = new Pcm16AudioLevelMeter();
        var observedAtUtc = new DateTimeOffset(2026, 4, 5, 12, 0, 0, TimeSpan.Zero);

        meter.ObserveFrame(TestFormat, CreatePayload(0, 20000, -20000, 0), observedAtUtc);

        var heldSnapshot = meter.GetSnapshot(observedAtUtc.AddMilliseconds(200));
        var decayedSnapshot = meter.GetSnapshot(observedAtUtc.AddMilliseconds(1500));

        Assert.InRange(heldSnapshot.DisplayPeakNormalized, 0.60d, 0.62d);
        Assert.True(decayedSnapshot.DisplayPeakNormalized < heldSnapshot.DisplayPeakNormalized);
        Assert.InRange(decayedSnapshot.DisplayPeakNormalized, 0d, 0.1d);
    }

    [Fact]
    public void ObserveFrameKeepsSilentPayloadInactive()
    {
        var meter = new Pcm16AudioLevelMeter();
        var observedAtUtc = new DateTimeOffset(2026, 4, 5, 12, 0, 0, TimeSpan.Zero);

        meter.ObserveFrame(TestFormat, CreatePayload(0, 0, 0, 0), observedAtUtc);
        var snapshot = meter.GetSnapshot(observedAtUtc);

        Assert.False(snapshot.SignalDetected);
        Assert.Equal(0, snapshot.PeakNormalized);
        Assert.Equal(0, snapshot.RmsNormalized);
        Assert.Null(snapshot.LastSignalAtUtc);
    }

    private static byte[] CreatePayload(params short[] samples)
    {
        var payload = new byte[samples.Length * sizeof(short)];

        for (var index = 0; index < samples.Length; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(index * sizeof(short), sizeof(short)), samples[index]);
        }

        return payload;
    }
}
