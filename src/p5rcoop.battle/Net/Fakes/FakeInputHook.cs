// Compiled only when the mod defines P5RCOOP_NET (see docs/NETWORKING.md, "Wiring the mod").
#if P5RCOOP_NET
using System.Collections.Generic;

namespace p5rcoop.battle.Net.Fakes;

/// <summary>
/// Emulates p5rpc.inputhook's behaviour (InputHook.KeyPressed at upstream commit 1e2e04e):
/// a poll with no host keys and nothing queued does not run the intercept at all; otherwise the list is
/// host keys + queued SendKey keys, the queue is cleared, the intercept may rewrite the list, and the
/// result is what the game processes. <see cref="Seen"/> records every key the "game" processed.
/// </summary>
public sealed class FakeInputHook : IInputSink
{
    private readonly object _gate = new();
    private readonly List<int> _queued = new();

    public event InputFilter? Intercept;

    /// <summary>Every key the game processed, across polls, in order.</summary>
    public List<int> Seen { get; } = new();

    public int QueuedCount
    {
        get { lock (_gate) return _queued.Count; }
    }

    public void SendKey(int keyCode)
    {
        lock (_gate)
            _queued.Add(keyCode);
    }

    /// <summary>One game input poll with the host holding <paramref name="hostKeys"/> (rising edges).</summary>
    public IReadOnlyList<int> Poll(params int[] hostKeys)
    {
        List<int> keys;
        lock (_gate)
        {
            if (hostKeys.Length == 0 && _queued.Count == 0)
                return new List<int>();
            keys = new List<int>(hostKeys);
            keys.AddRange(_queued);
            _queued.Clear();
        }
        Intercept?.Invoke(keys);
        lock (_gate)
            Seen.AddRange(keys);
        return keys;
    }
}
#endif
