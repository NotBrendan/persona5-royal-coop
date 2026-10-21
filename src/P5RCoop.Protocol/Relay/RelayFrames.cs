using System;

namespace P5RCoop.Protocol.Relay;

/// <summary>Wire ids of relay frames. The relay only understands these; game messages ride inside <see cref="RelayData"/>.</summary>
public enum RelayFrameKind : byte
{
    CreateRoom = 1,
    RoomCreated = 2,
    JoinRoom = 3,
    JoinAccepted = 4,
    Rejected = 5,
    PeerJoined = 6,
    PeerLeft = 7,
    Data = 8,
    Kick = 9,
    RoomClosed = 10,
}

public abstract record RelayFrame
{
    public abstract RelayFrameKind Kind { get; }
}

/// <summary>Host -&gt; relay: open a room, I am its host.</summary>
public sealed record CreateRoom : RelayFrame
{
    public override RelayFrameKind Kind => RelayFrameKind.CreateRoom;
}

/// <summary>Relay -&gt; host: room is open under this join code.</summary>
public sealed record RoomCreated(string Code) : RelayFrame
{
    public override RelayFrameKind Kind => RelayFrameKind.RoomCreated;
}

/// <summary>Friend -&gt; relay: join the room with this code.</summary>
public sealed record JoinRoom(string Code) : RelayFrame
{
    public override RelayFrameKind Kind => RelayFrameKind.JoinRoom;
}

/// <summary>Relay -&gt; friend: you are in, with this peer id (1..254, unique within the room).</summary>
public sealed record JoinAccepted(byte PeerId) : RelayFrame
{
    public override RelayFrameKind Kind => RelayFrameKind.JoinAccepted;
}

/// <summary>Relay -&gt; client: create/join refused; the relay closes the connection after this.</summary>
public sealed record Rejected(string Reason) : RelayFrame
{
    public override RelayFrameKind Kind => RelayFrameKind.Rejected;
}

/// <summary>Relay -&gt; host: a friend connected.</summary>
public sealed record PeerJoined(byte PeerId) : RelayFrame
{
    public override RelayFrameKind Kind => RelayFrameKind.PeerJoined;
}

/// <summary>Relay -&gt; host: a friend disconnected (closed, kicked or dropped).</summary>
public sealed record PeerLeft(byte PeerId) : RelayFrame
{
    public override RelayFrameKind Kind => RelayFrameKind.PeerLeft;
}

/// <summary>
/// Opaque payload (a serialized game <c>Message</c>).
/// Host -&gt; relay: PeerId is the target, <see cref="Broadcast"/> for every friend.
/// Relay -&gt; host: PeerId is the sender. Friend &lt;-&gt; relay: PeerId is ignored (always the host).
/// </summary>
public sealed record RelayData(byte PeerId, byte[] Payload) : RelayFrame
{
    public const byte Broadcast = 0;
    public override RelayFrameKind Kind => RelayFrameKind.Data;
}

/// <summary>Host -&gt; relay: disconnect this friend.</summary>
public sealed record Kick(byte PeerId) : RelayFrame
{
    public override RelayFrameKind Kind => RelayFrameKind.Kick;
}

/// <summary>Relay -&gt; friend: the host left; the relay closes the connection after this.</summary>
public sealed record RoomClosed(string Reason) : RelayFrame
{
    public override RelayFrameKind Kind => RelayFrameKind.RoomClosed;
}

/// <summary>
/// Relay frame encoding: <c>u8 relayVersion | u8 kind | body</c>. Versioned separately from the game
/// protocol so the relay never needs redeploying for game-message changes.
/// </summary>
public static class RelaySerializer
{
    public const byte Version = 1;

    public static byte[] Serialize(RelayFrame frame) => Serialize(frame, Version);

    public static byte[] Serialize(RelayFrame frame, byte version)
    {
        var w = new WireWriter();
        w.U8(version);
        w.U8((byte)frame.Kind);
        switch (frame)
        {
            case CreateRoom:
                break;
            case RoomCreated f:
                w.Str(f.Code);
                break;
            case JoinRoom f:
                w.Str(f.Code);
                break;
            case JoinAccepted f:
                w.U8(f.PeerId);
                break;
            case Rejected f:
                w.Str(f.Reason);
                break;
            case PeerJoined f:
                w.U8(f.PeerId);
                break;
            case PeerLeft f:
                w.U8(f.PeerId);
                break;
            case RelayData f:
                w.U8(f.PeerId);
                w.Bytes(f.Payload);
                break;
            case Kick f:
                w.U8(f.PeerId);
                break;
            case RoomClosed f:
                w.Str(f.Reason);
                break;
            default:
                throw new ArgumentException($"Unknown relay frame {frame.GetType().Name}");
        }
        return w.ToArray();
    }

    public static RelayFrame Deserialize(ReadOnlySpan<byte> data)
    {
        if (data.Length > ProtocolInfo.MaxFrameSize)
            throw new ProtocolException($"Relay frame too large ({data.Length} bytes)");
        var r = new WireReader(data);
        var version = r.U8();
        if (version != Version)
            throw new ProtocolVersionException(version, Version);
        var kind = (RelayFrameKind)r.U8();
        RelayFrame frame = kind switch
        {
            RelayFrameKind.CreateRoom => new CreateRoom(),
            RelayFrameKind.RoomCreated => new RoomCreated(r.Str()),
            RelayFrameKind.JoinRoom => new JoinRoom(r.Str()),
            RelayFrameKind.JoinAccepted => new JoinAccepted(r.U8()),
            RelayFrameKind.Rejected => new Rejected(r.Str()),
            RelayFrameKind.PeerJoined => new PeerJoined(r.U8()),
            RelayFrameKind.PeerLeft => new PeerLeft(r.U8()),
            RelayFrameKind.Data => new RelayData(r.U8(), r.Bytes()),
            RelayFrameKind.Kick => new Kick(r.U8()),
            RelayFrameKind.RoomClosed => new RoomClosed(r.Str()),
            _ => throw new ProtocolException($"Unknown relay frame kind {(byte)kind}"),
        };
        r.EnsureEnd();
        return frame;
    }
}
