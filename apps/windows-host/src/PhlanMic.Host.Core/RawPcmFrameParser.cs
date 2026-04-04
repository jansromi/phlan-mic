namespace PhlanMic.Host.Core;

public sealed class RawPcmFrameParser
{
    private readonly AudioFormat format;
    private byte[] pendingBuffer;
    private int pendingCount;
    private long nextSequenceNumber;

    public RawPcmFrameParser(AudioFormat format, long startingSequenceNumber = 1)
    {
        ArgumentNullException.ThrowIfNull(format);
        format.Validate();

        if (startingSequenceNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startingSequenceNumber),
                "Starting sequence number must be greater than zero.");
        }

        this.format = format;
        pendingBuffer = new byte[Math.Max(format.BytesPerFrame, 256)];
        nextSequenceNumber = startingSequenceNumber;
    }

    public int PendingBytes => pendingCount;

    public IReadOnlyList<AudioFrame> ParseBytes(ReadOnlySpan<byte> bytes, DateTimeOffset capturedAtUtc)
    {
        if (bytes.Length == 0)
        {
            return Array.Empty<AudioFrame>();
        }

        EnsureCapacity(pendingCount + bytes.Length);
        bytes.CopyTo(pendingBuffer.AsSpan(pendingCount));
        pendingCount += bytes.Length;

        var frames = new List<AudioFrame>(pendingCount / format.BytesPerFrame);
        var offset = 0;

        while (pendingCount - offset >= format.BytesPerFrame)
        {
            var payload = new byte[format.BytesPerFrame];
            pendingBuffer.AsSpan(offset, format.BytesPerFrame).CopyTo(payload);
            frames.Add(new AudioFrame(nextSequenceNumber++, format, payload, capturedAtUtc));
            offset += format.BytesPerFrame;
        }

        if (offset > 0)
        {
            pendingCount -= offset;

            if (pendingCount > 0)
            {
                Array.Copy(pendingBuffer, offset, pendingBuffer, 0, pendingCount);
            }
        }

        return frames;
    }

    private void EnsureCapacity(int requiredCapacity)
    {
        if (requiredCapacity <= pendingBuffer.Length)
        {
            return;
        }

        var newCapacity = pendingBuffer.Length;
        while (newCapacity < requiredCapacity)
        {
            newCapacity *= 2;
        }

        Array.Resize(ref pendingBuffer, newCapacity);
    }
}
