using System;
using System.Collections.Generic;

namespace P5RCoop.Protocol;

/// <summary>
/// Compact binary encoding of <see cref="Message"/>s.
/// Frame layout: <c>u16 protocolVersion | u8 type | body</c>, all little-endian.
/// Frames from another protocol version are refused with <see cref="ProtocolVersionException"/>
/// before the body is looked at, so the version header must never move.
/// </summary>
public static class MessageSerializer
{
    public const int HeaderSize = 3;

    public static byte[] Serialize(Message message) => Serialize(message, ProtocolInfo.Version);

    /// <summary>Serialize with an explicit header version. Only tests and version-mismatch replies need this.</summary>
    public static byte[] Serialize(Message message, ushort version)
    {
        var w = new WireWriter();
        w.U16(version);
        w.U8((byte)message.Type);
        switch (message)
        {
            case Hello m:
                w.U16(m.ProtocolVersion);
                w.Str(m.Name);
                break;
            case Welcome m:
                w.Bool(m.Accepted);
                w.Str(m.Reason);
                w.U8(m.PlayerId);
                w.Str(m.HostName);
                break;
            case SeatClaim m:
                w.U8(m.Slot);
                break;
            case SeatClaimResult m:
                w.U8(m.Slot);
                w.Bool(m.Accepted);
                w.Str(m.Reason);
                break;
            case SeatAssignments m:
                if (m.Seats.Count > byte.MaxValue)
                    throw new ArgumentException("Too many seats");
                w.U8((byte)m.Seats.Count);
                foreach (var seat in m.Seats)
                {
                    w.U8(seat.Slot);
                    w.Str(seat.Label);
                    w.U8(seat.OwnerId);
                    w.Str(seat.OwnerName);
                }
                break;
            case TurnChanged m:
                w.Bool(m.Slot.HasValue);
                w.U8(m.Slot ?? 0);
                break;
            case KeyPress m:
                w.I32(m.Key);
                w.U32(m.Sequence);
                break;
            case Ping m:
                w.U32(m.Nonce);
                w.I64(m.TimestampMs);
                break;
            case Pong m:
                w.U32(m.Nonce);
                w.I64(m.TimestampMs);
                break;
            case Leave m:
                w.Str(m.Reason);
                break;
            default:
                throw new ArgumentException($"Unknown message {message.GetType().Name}");
        }
        return w.ToArray();
    }

    /// <summary>Reads only the version header. Returns false if the frame is too short.</summary>
    public static bool TryPeekVersion(ReadOnlySpan<byte> frame, out ushort version)
    {
        version = 0;
        if (frame.Length < 2)
            return false;
        version = (ushort)(frame[0] | (frame[1] << 8));
        return true;
    }

    public static Message Deserialize(ReadOnlySpan<byte> frame)
    {
        if (frame.Length > ProtocolInfo.MaxFrameSize)
            throw new ProtocolException($"Frame too large ({frame.Length} bytes)");
        var r = new WireReader(frame);
        var version = r.U16();
        if (version != ProtocolInfo.Version)
            throw new ProtocolVersionException(version, ProtocolInfo.Version);
        var type = (MessageType)r.U8();
        Message message = type switch
        {
            MessageType.Hello => new Hello(r.U16(), r.Str()),
            MessageType.Welcome => new Welcome(r.Bool(), r.Str(), r.U8(), r.Str()),
            MessageType.SeatClaim => new SeatClaim(r.U8()),
            MessageType.SeatClaimResult => new SeatClaimResult(r.U8(), r.Bool(), r.Str()),
            MessageType.SeatAssignments => ReadSeats(ref r),
            MessageType.TurnChanged => ReadTurn(ref r),
            MessageType.KeyPress => new KeyPress(r.I32(), r.U32()),
            MessageType.Ping => new Ping(r.U32(), r.I64()),
            MessageType.Pong => new Pong(r.U32(), r.I64()),
            MessageType.Leave => new Leave(r.Str()),
            _ => throw new ProtocolException($"Unknown message type {(byte)type}"),
        };
        r.EnsureEnd();
        return message;
    }

    private static SeatAssignments ReadSeats(ref WireReader r)
    {
        int count = r.U8();
        var seats = new List<Seat>(count);
        for (var i = 0; i < count; i++)
            seats.Add(new Seat(r.U8(), r.Str(), r.U8(), r.Str()));
        return new SeatAssignments(seats);
    }

    private static TurnChanged ReadTurn(ref WireReader r)
    {
        var has = r.Bool();
        var slot = r.U8();
        return new TurnChanged(has ? slot : null);
    }
}
