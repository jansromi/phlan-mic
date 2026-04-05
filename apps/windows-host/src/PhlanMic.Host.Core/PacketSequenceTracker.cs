namespace PhlanMic.Host.Core;

internal readonly record struct PacketSequenceAssessment(bool IsDuplicate, bool IsOutOfOrder);

internal sealed class PacketSequenceTracker
{
    private readonly int capacity;
    private readonly HashSet<long> recentSequences = [];
    private readonly Queue<long> sequenceOrder = [];
    private long highestSequenceSeen;

    public PacketSequenceTracker(int capacity = 256)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");
        }

        this.capacity = capacity;
    }

    public PacketSequenceAssessment Record(long sequenceNumber)
    {
        if (recentSequences.Contains(sequenceNumber))
        {
            return new PacketSequenceAssessment(IsDuplicate: true, IsOutOfOrder: false);
        }

        var isOutOfOrder = highestSequenceSeen > 0 && sequenceNumber < highestSequenceSeen;
        if (sequenceNumber > highestSequenceSeen)
        {
            highestSequenceSeen = sequenceNumber;
        }

        recentSequences.Add(sequenceNumber);
        sequenceOrder.Enqueue(sequenceNumber);

        while (sequenceOrder.Count > capacity)
        {
            recentSequences.Remove(sequenceOrder.Dequeue());
        }

        return new PacketSequenceAssessment(IsDuplicate: false, IsOutOfOrder: isOutOfOrder);
    }

    public void Reset()
    {
        highestSequenceSeen = 0;
        recentSequences.Clear();
        sequenceOrder.Clear();
    }
}
