// Compiled only when the mod defines P5RCOOP_NET (see docs/NETWORKING.md, "Wiring the mod").
#if P5RCOOP_NET
using System;
using System.Collections.Generic;

namespace p5rcoop.battle.Net;

/// <summary>A party slot friends may claim. <see cref="Slot"/> must match what the <see cref="ITurnSource"/> reports.</summary>
public sealed record SeatDefinition(byte Slot, string Label);

public sealed class HostSessionOptions
{
    /// <summary>Shown to friends in the Welcome message.</summary>
    public string HostName { get; init; } = "Host";

    /// <summary>
    /// Slots friends may claim. Slots not listed here (e.g. Joker) are always played by the host.
    /// </summary>
    public IReadOnlyList<SeatDefinition> Seats { get; init; } = Array.Empty<SeatDefinition>();

    /// <summary>How many out-of-order key presses to hold per friend before skipping a gap.</summary>
    public int MaxReorderPending { get; init; } = 16;

    /// <summary>Diagnostics sink, e.g. the Reloaded logger. Called while the session lock is held.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Parses "1=Ryuji,2=Ann" (slot=label pairs), the format used in docs/NETWORKING.md for mod config.</summary>
    public static IReadOnlyList<SeatDefinition> ParseSeats(string text)
    {
        var seats = new List<SeatDefinition>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            var slotText = eq < 0 ? part : part[..eq];
            var label = eq < 0 ? $"Slot {slotText}" : part[(eq + 1)..].Trim();
            if (!byte.TryParse(slotText.Trim(), out var slot))
                throw new FormatException($"Bad seat '{part}', expected slot=label");
            seats.Add(new SeatDefinition(slot, label));
        }
        return seats;
    }
}
#endif
