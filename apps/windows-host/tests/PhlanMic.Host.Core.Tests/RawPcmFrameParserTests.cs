using PhlanMic.Host.Core;

namespace PhlanMic.Host.Core.Tests;

public sealed class RawPcmFrameParserTests
{
    [Fact]
    public void ParseBytesBuffersPartialFramesAcrossReads()
    {
        var format = AudioFormat.CreateMvpDefault();
        var parser = new RawPcmFrameParser(format);
        var sourceBytes = Enumerable.Range(0, (format.BytesPerFrame * 2) + 17)
            .Select(index => (byte)(index % byte.MaxValue))
            .ToArray();

        var firstBatch = parser.ParseBytes(sourceBytes.AsSpan(0, format.BytesPerFrame / 2), DateTimeOffset.UtcNow);
        var secondBatch = parser.ParseBytes(sourceBytes.AsSpan(format.BytesPerFrame / 2), DateTimeOffset.UtcNow);

        Assert.Empty(firstBatch);
        Assert.Equal(17, parser.PendingBytes);
        Assert.Equal(2, secondBatch.Count);
        Assert.Equal(1, secondBatch[0].SequenceNumber);
        Assert.Equal(2, secondBatch[1].SequenceNumber);
        Assert.Equal(sourceBytes.Take(format.BytesPerFrame).ToArray(), secondBatch[0].Payload);
        Assert.Equal(
            sourceBytes.Skip(format.BytesPerFrame).Take(format.BytesPerFrame).ToArray(),
            secondBatch[1].Payload);
    }

    [Fact]
    public void ParseBytesStartsSequencesAtConfiguredValue()
    {
        var format = AudioFormat.CreateMvpDefault();
        var parser = new RawPcmFrameParser(format, startingSequenceNumber: 42);
        var bytes = new byte[format.BytesPerFrame];

        var frames = parser.ParseBytes(bytes, DateTimeOffset.UtcNow);

        var frame = Assert.Single(frames);
        Assert.Equal(42, frame.SequenceNumber);
        Assert.Equal(0, parser.PendingBytes);
    }
}
