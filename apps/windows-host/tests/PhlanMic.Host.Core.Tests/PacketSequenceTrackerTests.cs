using PhlanMic.Host.Core;

namespace PhlanMic.Host.Core.Tests;

public sealed class PacketSequenceTrackerTests
{
    [Fact]
    public void RecordFlagsDuplicateSequenceNumbers()
    {
        var tracker = new PacketSequenceTracker(capacity: 8);

        Assert.False(tracker.Record(10).IsDuplicate);

        var duplicate = tracker.Record(10);

        Assert.True(duplicate.IsDuplicate);
        Assert.False(duplicate.IsOutOfOrder);
    }

    [Fact]
    public void RecordFlagsOutOfOrderSequenceNumbers()
    {
        var tracker = new PacketSequenceTracker(capacity: 8);

        tracker.Record(10);
        tracker.Record(12);

        var outOfOrder = tracker.Record(11);

        Assert.False(outOfOrder.IsDuplicate);
        Assert.True(outOfOrder.IsOutOfOrder);
    }
}
