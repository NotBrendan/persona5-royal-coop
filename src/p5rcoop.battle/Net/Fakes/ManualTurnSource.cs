// Compiled only when the mod defines P5RCOOP_NET (see docs/NETWORKING.md, "Wiring the mod").
#if P5RCOOP_NET
using System;

namespace p5rcoop.battle.Net.Fakes;

/// <summary>
/// Turn source driven by code: tests call <see cref="SetTurn"/>; in the game it can be bound to debug
/// hotkeys until the real "acting party member" hook exists.
/// </summary>
public sealed class ManualTurnSource : ITurnSource
{
    private readonly object _gate = new();
    private byte? _current;

    public byte? CurrentSlot
    {
        get { lock (_gate) return _current; }
    }

    public event Action<byte?>? TurnChanged;

    public void SetTurn(byte? slot)
    {
        lock (_gate)
        {
            if (_current == slot)
                return;
            _current = slot;
        }
        TurnChanged?.Invoke(slot);
    }

    public void EndBattle() => SetTurn(null);
}
#endif
