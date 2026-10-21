// Compiled only when the mod defines P5RCOOP_NET (see docs/NETWORKING.md, "Wiring the mod").
#if P5RCOOP_NET
using System;

namespace p5rcoop.battle.Net;

/// <summary>
/// Tells the host session whose battle command menu is open. Implemented by the game hook once the
/// "which party member is acting" address is known; <see cref="Fakes.ManualTurnSource"/> until then.
/// Slot ids are opaque bytes; they only have to match the ids in <see cref="HostSessionOptions.Seats"/>.
/// </summary>
public interface ITurnSource
{
    /// <summary>Slot whose command menu is open, or null outside battle / during enemy turns.</summary>
    byte? CurrentSlot { get; }

    /// <summary>Raised with the new <see cref="CurrentSlot"/> whenever it changes. Any thread.</summary>
    event Action<byte?>? TurnChanged;
}
#endif
