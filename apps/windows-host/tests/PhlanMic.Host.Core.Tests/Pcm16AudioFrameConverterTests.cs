using System.Buffers.Binary;
using PhlanMic.Host.Core;

namespace PhlanMic.Host.Core.Tests;

public sealed class Pcm16AudioFrameConverterTests
{
    [Fact]
    public void ConvertPayloadDuplicatesMonoSamplesWhenTargetIsStereo()
    {
        var sourceFormat = new AudioFormat
        {
            SampleRate = 400,
            Channels = 1,
            BitsPerSample = 16,
            FrameDurationMs = 10
        };
        var targetFormat = sourceFormat with { Channels = 2 };
        var sourcePayload = CreatePayload(100, 200, 300, 400);

        var converted = Pcm16AudioFrameConverter.ConvertPayload(sourceFormat, sourcePayload, targetFormat);
        var samples = DecodeSamples(converted);

        Assert.Equal(new short[] { 100, 100, 200, 200, 300, 300, 400, 400 }, samples);
    }

    [Fact]
    public void ConvertPayloadResamplesFramesWhenSampleRateChanges()
    {
        var sourceFormat = new AudioFormat
        {
            SampleRate = 400,
            Channels = 1,
            BitsPerSample = 16,
            FrameDurationMs = 10
        };
        var targetFormat = sourceFormat with { SampleRate = 800 };
        var sourcePayload = CreatePayload(0, 1000, 2000, 3000);

        var converted = Pcm16AudioFrameConverter.ConvertPayload(sourceFormat, sourcePayload, targetFormat);
        var samples = DecodeSamples(converted);

        Assert.Equal(targetFormat.BytesPerFrame, converted.Length);
        Assert.Equal(0, samples[0]);
        Assert.Equal(3000, samples[^1]);
        Assert.InRange(samples[1], 400, 450);
        Assert.InRange(samples[3], 1250, 1300);
        Assert.InRange(samples[5], 2100, 2150);
    }

    [Fact]
    public void ConvertFramePreservesMetadataAndAppliesTargetFormat()
    {
        var sourceFormat = new AudioFormat
        {
            SampleRate = 400,
            Channels = 1,
            BitsPerSample = 16,
            FrameDurationMs = 10
        };
        var targetFormat = sourceFormat with { Channels = 2 };
        var capturedAtUtc = DateTimeOffset.UtcNow;
        var frame = new AudioFrame(42, sourceFormat, CreatePayload(1, 2, 3, 4), capturedAtUtc);

        var converted = Pcm16AudioFrameConverter.ConvertFrame(frame, targetFormat);

        Assert.Equal(42, converted.SequenceNumber);
        Assert.Equal(capturedAtUtc, converted.CapturedAtUtc);
        Assert.Equal(targetFormat, converted.Format);
        Assert.Equal(targetFormat.BytesPerFrame, converted.Payload.Length);
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

    private static short[] DecodeSamples(byte[] payload)
    {
        var samples = new short[payload.Length / sizeof(short)];

        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(index * sizeof(short), sizeof(short)));
        }

        return samples;
    }
}
