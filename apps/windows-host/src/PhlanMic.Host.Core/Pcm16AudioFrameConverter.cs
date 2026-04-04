using System.Buffers.Binary;

namespace PhlanMic.Host.Core;

public static class Pcm16AudioFrameConverter
{
    public static AudioFrame ConvertFrame(AudioFrame frame, AudioFormat targetFormat)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(targetFormat);

        if (frame.Format == targetFormat)
        {
            return frame;
        }

        return new AudioFrame(
            frame.SequenceNumber,
            targetFormat,
            ConvertPayload(frame.Format, frame.Payload, targetFormat),
            frame.CapturedAtUtc);
    }

    public static byte[] ConvertPayload(
        AudioFormat sourceFormat,
        ReadOnlySpan<byte> sourcePayload,
        AudioFormat targetFormat)
    {
        ArgumentNullException.ThrowIfNull(sourceFormat);
        ArgumentNullException.ThrowIfNull(targetFormat);

        sourceFormat.Validate();
        targetFormat.Validate();

        if (sourcePayload.Length != sourceFormat.BytesPerFrame)
        {
            throw new ArgumentException(
                $"Source payload length {sourcePayload.Length} does not match expected frame size {sourceFormat.BytesPerFrame}.",
                nameof(sourcePayload));
        }

        if (sourceFormat.FrameDurationMs != targetFormat.FrameDurationMs)
        {
            throw new InvalidOperationException("PCM frame conversion requires matching frame durations.");
        }

        if (sourceFormat.BitsPerSample is not 16 || targetFormat.BitsPerSample is not 16)
        {
            throw new InvalidOperationException("PCM frame conversion only supports 16-bit signed PCM.");
        }

        if (sourceFormat == targetFormat)
        {
            return sourcePayload.ToArray();
        }

        var sourceChannels = DecodeChannels(sourceFormat, sourcePayload);
        var resampledChannels = ResampleChannels(sourceChannels, targetFormat.SamplesPerFrame);
        return EncodeChannels(MapChannels(resampledChannels, targetFormat.Channels), targetFormat);
    }

    private static short[][] DecodeChannels(AudioFormat sourceFormat, ReadOnlySpan<byte> payload)
    {
        var channels = new short[sourceFormat.Channels][];

        for (var channelIndex = 0; channelIndex < sourceFormat.Channels; channelIndex++)
        {
            channels[channelIndex] = new short[sourceFormat.SamplesPerFrame];
        }

        for (var sampleIndex = 0; sampleIndex < sourceFormat.SamplesPerFrame; sampleIndex++)
        {
            for (var channelIndex = 0; channelIndex < sourceFormat.Channels; channelIndex++)
            {
                var offset = ((sampleIndex * sourceFormat.Channels) + channelIndex) * sourceFormat.BytesPerSample;
                channels[channelIndex][sampleIndex] = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(offset, sizeof(short)));
            }
        }

        return channels;
    }

    private static short[][] ResampleChannels(short[][] sourceChannels, int targetSamplesPerFrame)
    {
        var resampledChannels = new short[sourceChannels.Length][];

        for (var channelIndex = 0; channelIndex < sourceChannels.Length; channelIndex++)
        {
            resampledChannels[channelIndex] = ResampleLinear(sourceChannels[channelIndex], targetSamplesPerFrame);
        }

        return resampledChannels;
    }

    private static short[][] MapChannels(short[][] sourceChannels, int targetChannelCount)
    {
        if (sourceChannels.Length == targetChannelCount)
        {
            return sourceChannels;
        }

        var targetChannels = new short[targetChannelCount][];

        if (sourceChannels.Length == 1)
        {
            for (var channelIndex = 0; channelIndex < targetChannelCount; channelIndex++)
            {
                targetChannels[channelIndex] = sourceChannels[0].ToArray();
            }

            return targetChannels;
        }

        if (targetChannelCount == 1)
        {
            var mixed = new short[sourceChannels[0].Length];

            for (var sampleIndex = 0; sampleIndex < mixed.Length; sampleIndex++)
            {
                var sum = 0;

                for (var channelIndex = 0; channelIndex < sourceChannels.Length; channelIndex++)
                {
                    sum += sourceChannels[channelIndex][sampleIndex];
                }

                mixed[sampleIndex] = (short)(sum / sourceChannels.Length);
            }

            targetChannels[0] = mixed;
            return targetChannels;
        }

        for (var channelIndex = 0; channelIndex < targetChannelCount; channelIndex++)
        {
            var mappedSourceIndex = (int)Math.Round(
                channelIndex * (sourceChannels.Length - 1d) / Math.Max(targetChannelCount - 1d, 1d));
            targetChannels[channelIndex] = sourceChannels[mappedSourceIndex].ToArray();
        }

        return targetChannels;
    }

    private static short[] ResampleLinear(short[] sourceSamples, int targetSampleCount)
    {
        if (sourceSamples.Length == targetSampleCount)
        {
            return sourceSamples.ToArray();
        }

        if (sourceSamples.Length == 1)
        {
            var duplicated = new short[targetSampleCount];
            Array.Fill(duplicated, sourceSamples[0]);
            return duplicated;
        }

        var result = new short[targetSampleCount];
        var scale = (sourceSamples.Length - 1d) / Math.Max(targetSampleCount - 1d, 1d);

        for (var targetIndex = 0; targetIndex < targetSampleCount; targetIndex++)
        {
            var sourcePosition = targetIndex * scale;
            var leftIndex = (int)Math.Floor(sourcePosition);
            var rightIndex = Math.Min(leftIndex + 1, sourceSamples.Length - 1);
            var fraction = sourcePosition - leftIndex;
            var interpolated = sourceSamples[leftIndex] + ((sourceSamples[rightIndex] - sourceSamples[leftIndex]) * fraction);
            result[targetIndex] = (short)Math.Clamp(Math.Round(interpolated), short.MinValue, short.MaxValue);
        }

        return result;
    }

    private static byte[] EncodeChannels(short[][] channels, AudioFormat targetFormat)
    {
        var payload = new byte[targetFormat.BytesPerFrame];

        for (var sampleIndex = 0; sampleIndex < targetFormat.SamplesPerFrame; sampleIndex++)
        {
            for (var channelIndex = 0; channelIndex < targetFormat.Channels; channelIndex++)
            {
                var offset = ((sampleIndex * targetFormat.Channels) + channelIndex) * targetFormat.BytesPerSample;
                BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(offset, sizeof(short)), channels[channelIndex][sampleIndex]);
            }
        }

        return payload;
    }
}
