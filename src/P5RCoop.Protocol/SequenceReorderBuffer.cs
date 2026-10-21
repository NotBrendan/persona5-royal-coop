using System.Collections.Generic;

namespace P5RCoop.Protocol;

/// <summary>Outcome of <see cref="SequenceReorderBuffer{T}.Push"/>.</summary>
public enum SequenceResult
{
    /// <summary>The item (and possibly buffered successors) were released in order.</summary>
    Delivered,
    /// <summary>The item arrived early and is held until the gap before it fills.</summary>
    Buffered,
    /// <summary>Already delivered or already buffered: dropped.</summary>
    Duplicate,
}

/// <summary>
/// Releases items strictly in sequence order (first sequence is 1) and drops duplicates.
/// If more than <c>maxPending</c> items wait behind a gap, the gap is given up on (counted in
/// <see cref="Skipped"/>) so a lost item over an unreliable transport cannot stall input forever.
/// Not thread-safe.
/// </summary>
public sealed class SequenceReorderBuffer<T>
{
    private readonly SortedDictionary<uint, T> _pending = new();
    private readonly int _maxPending;

    public SequenceReorderBuffer(int maxPending = 16)
    {
        _maxPending = maxPending < 1 ? 1 : maxPending;
    }

    /// <summary>Next sequence number that will be released.</summary>
    public uint NextExpected { get; private set; } = 1;

    public int PendingCount => _pending.Count;

    /// <summary>Number of sequence numbers abandoned because a gap never filled.</summary>
    public long Skipped { get; private set; }

    public SequenceResult Push(uint sequence, T item, List<T> released)
    {
        if (sequence < NextExpected || _pending.ContainsKey(sequence))
            return SequenceResult.Duplicate;

        if (sequence != NextExpected)
        {
            _pending.Add(sequence, item);
            if (_pending.Count <= _maxPending)
                return SequenceResult.Buffered;

            // Give up on the gap: jump to the lowest held sequence.
            using var e = _pending.Keys.GetEnumerator();
            e.MoveNext();
            Skipped += e.Current - NextExpected;
            NextExpected = e.Current;
            Drain(released);
            return SequenceResult.Delivered;
        }

        released.Add(item);
        NextExpected++;
        Drain(released);
        return SequenceResult.Delivered;
    }

    private void Drain(List<T> released)
    {
        while (_pending.Remove(NextExpected, out var next))
        {
            released.Add(next);
            NextExpected++;
        }
    }
}
