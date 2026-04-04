using System.Buffers.Binary;
using PhlanMic.Host.Core;

namespace PhlanMic.DebugTcpSender;

internal sealed class PcmSignalGenerator
{
    private readonly AudioFormat format;
    private readonly string signalMode;
    private readonly int signalFrequencyHz;
    private double samplePosition;

    public PcmSignalGenerator(AudioFormat format, string signalMode, int signalFrequencyHz)
    {
        ArgumentNullException.ThrowIfNull(format);

        this.format = format;
        this.signalMode = signalMode;
        this.signalFrequencyHz = signalFrequencyHz;
    }

    public byte[] CreateFramePayload()
    {
        var payload = new byte[format.BytesPerFrame];
        if (string.Equals(signalMode, "silence", StringComparison.OrdinalIgnoreCase))
        {
            return payload;
        }

        var amplitude = short.MaxValue / 4.0;
        var twoPi = Math.PI * 2;

        for (var sampleIndex = 0; sampleIndex < format.SamplesPerFrame; sampleIndex++)
        {
            var value = Math.Sin(twoPi * signalFrequencyHz * samplePosition / format.SampleRate) * amplitude;
            var pcmSample = (short)Math.Round(value);

            for (var channelIndex = 0; channelIndex < format.Channels; channelIndex++)
            {
                var offset = ((sampleIndex * format.Channels) + channelIndex) * format.BytesPerSample;
                BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(offset, sizeof(short)), pcmSample);
            }

            samplePosition++;
        }

        return payload;
    }
}
