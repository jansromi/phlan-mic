namespace PhlanMic.Host.Core;

public sealed class AudioFrame
{
    public AudioFrame(long sequenceNumber, AudioFormat format, ReadOnlyMemory<byte> payload, DateTimeOffset capturedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(format);
        format.Validate();

        if (payload.Length != format.BytesPerFrame)
        {
            throw new ArgumentException(
                $"Audio frame payload length {payload.Length} does not match expected frame size {format.BytesPerFrame}.",
                nameof(payload));
        }

        SequenceNumber = sequenceNumber;
        Format = format;
        Payload = payload.ToArray();
        CapturedAtUtc = capturedAtUtc;
    }

    public long SequenceNumber { get; }

    public AudioFormat Format { get; }

    public byte[] Payload { get; }

    public DateTimeOffset CapturedAtUtc { get; }
}

