using System.Collections.Generic;

namespace PhlanMic.Host.Core;

public sealed class AudioFrameQueue
{
    private readonly Queue<AudioFrame> frames = new();
    private readonly object gate = new();
    private readonly int capacity;

    public AudioFrameQueue(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Queue capacity must be greater than zero.");
        }

        this.capacity = capacity;
    }

    public long DroppedFrames { get; private set; }

    public int Count
    {
        get
        {
            lock (gate)
            {
                return frames.Count;
            }
        }
    }

    public bool TryEnqueue(AudioFrame frame, bool dropOldestWhenFull, out bool droppedOldest)
    {
        ArgumentNullException.ThrowIfNull(frame);

        lock (gate)
        {
            droppedOldest = false;

            if (frames.Count >= capacity)
            {
                if (!dropOldestWhenFull)
                {
                    DroppedFrames++;
                    return false;
                }

                frames.Dequeue();
                DroppedFrames++;
                droppedOldest = true;
            }

            frames.Enqueue(frame);
            return true;
        }
    }

    public bool TryDequeue(out AudioFrame? frame)
    {
        lock (gate)
        {
            if (frames.Count == 0)
            {
                frame = null;
                return false;
            }

            frame = frames.Dequeue();
            return true;
        }
    }
}

