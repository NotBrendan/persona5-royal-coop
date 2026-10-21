using System.Collections.Generic;

namespace P5RCoop.Protocol;

/// <summary>Wire ids of the game messages. Never renumber; add new ids at the end.</summary>
public enum MessageType : byte
{
    Hello = 1,
    Welcome = 2,
    SeatClaim = 3,
    SeatClaimResult = 4,
    SeatAssignments = 5,
    TurnChanged = 6,
    KeyPress = 7,
    Ping = 8,
    Pong = 9,
    Leave = 10,
}

/// <summary>A message between the host (game side) and a friend. Routed through the relay as opaque bytes.</summary>
public abstract record Message
{
    public abstract MessageType Type { get; }
}

/// <summary>Friend -&gt; host, first message after joining a room ("join"): who I am and which protocol I speak.</summary>
public sealed record Hello(ushort ProtocolVersion, string Name) : Message
{
    public override MessageType Type => MessageType.Hello;
}

/// <summary>Host -&gt; friend, answer to <see cref="Hello"/>. When rejected the host kicks the peer right after.</summary>
public sealed record Welcome(bool Accepted, string Reason, byte PlayerId, string HostName) : Message
{
    public override MessageType Type => MessageType.Welcome;
}

/// <summary>Friend -&gt; host: "I want to control this party slot". Slot ids are opaque bytes chosen by the host.</summary>
public sealed record SeatClaim(byte Slot) : Message
{
    public override MessageType Type => MessageType.SeatClaim;
}

/// <summary>Host -&gt; friend, answer to <see cref="SeatClaim"/>.</summary>
public sealed record SeatClaimResult(byte Slot, bool Accepted, string Reason) : Message
{
    public override MessageType Type => MessageType.SeatClaimResult;
}

/// <summary>One claimable party slot. <see cref="OwnerId"/> 0 means nobody owns it (the host plays it).</summary>
public sealed record Seat(byte Slot, string Label, byte OwnerId, string OwnerName)
{
    public const byte NoOwner = 0;
    public bool IsFree => OwnerId == NoOwner;
}

/// <summary>Host -&gt; everyone: the full seat table. Sent after every change.</summary>
public sealed record SeatAssignments(IReadOnlyList<Seat> Seats) : Message
{
    public override MessageType Type => MessageType.SeatAssignments;
}

/// <summary>Host -&gt; everyone: the slot whose command menu is open now, or null (no battle / not a party turn).</summary>
public sealed record TurnChanged(byte? Slot) : Message
{
    public override MessageType Type => MessageType.TurnChanged;
}

/// <summary>
/// Friend -&gt; host: one key press (rising edge). <see cref="Key"/> is a <see cref="KeyCodes"/> value.
/// <see cref="Sequence"/> starts at 1 per connection and increases by 1 per key; the host uses it to
/// keep order and drop duplicates.
/// </summary>
public sealed record KeyPress(int Key, uint Sequence) : Message
{
    public override MessageType Type => MessageType.KeyPress;
}

/// <summary>Liveness / latency probe. The receiver answers with a <see cref="Pong"/> echoing both fields.</summary>
public sealed record Ping(uint Nonce, long TimestampMs) : Message
{
    public override MessageType Type => MessageType.Ping;
}

public sealed record Pong(uint Nonce, long TimestampMs) : Message
{
    public override MessageType Type => MessageType.Pong;
}

/// <summary>Either side: I'm leaving (graceful). The host treats it like a disconnect.</summary>
public sealed record Leave(string Reason) : Message
{
    public override MessageType Type => MessageType.Leave;
}
