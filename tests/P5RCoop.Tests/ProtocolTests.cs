using P5RCoop.Protocol;
using P5RCoop.Protocol.Relay;
using p5rpc.inputhook.interfaces;

namespace P5RCoop.Tests;

public class ProtocolTests
{
    public static TheoryData<Message> AllMessages() => new()
    {
        new Hello(ProtocolInfo.Version, "Ryuji's friend ✓"),
        new Welcome(true, "", 3, "Host"),
        new Welcome(false, "Protocol mismatch", 0, "Host"),
        new SeatClaim(2),
        new SeatClaimResult(2, false, "taken"),
        new SeatAssignments(new[] { new Seat(1, "Ryuji", 3, "Player3"), new Seat(2, "Ann", 0, "") }),
        new SeatAssignments(Array.Empty<Seat>()),
        new TurnChanged(1),
        new TurnChanged(0),
        new TurnChanged(null),
        new KeyPress(KeyCodes.ENTER, 1),
        new KeyPress(KeyCodes.WIN, uint.MaxValue),
        new Ping(7, long.MaxValue),
        new Pong(7, -1),
        new Leave("bye"),
    };

    [Theory]
    [MemberData(nameof(AllMessages))]
    public void Every_message_round_trips(Message message)
    {
        var bytes = MessageSerializer.Serialize(message);
        var back = MessageSerializer.Deserialize(bytes);

        Assert.Equal(message.Type, back.Type);
        if (message is SeatAssignments seats)
            Assert.Equal(seats.Seats, ((SeatAssignments)back).Seats);
        else
            Assert.Equal(message, back);
    }

    [Fact]
    public void Frames_are_compact()
    {
        // u16 version + u8 type + i32 key + u32 sequence
        Assert.Equal(11, MessageSerializer.Serialize(new KeyPress(KeyCodes.UP, 42)).Length);
        Assert.Equal(5, MessageSerializer.Serialize(new TurnChanged(1)).Length);
    }

    [Fact]
    public void Frame_from_other_protocol_version_is_rejected()
    {
        var bytes = MessageSerializer.Serialize(new Hello(2, "future"), version: (ushort)(ProtocolInfo.Version + 1));

        var e = Assert.Throws<ProtocolVersionException>(() => MessageSerializer.Deserialize(bytes));
        Assert.Equal(ProtocolInfo.Version + 1, e.RemoteVersion);
        Assert.Equal(ProtocolInfo.Version, e.LocalVersion);
    }

    [Fact]
    public void Truncated_trailing_and_unknown_frames_are_rejected()
    {
        var good = MessageSerializer.Serialize(new KeyPress(KeyCodes.A, 1));
        Assert.Throws<ProtocolException>(() => MessageSerializer.Deserialize(good.AsSpan(0, good.Length - 1)));
        Assert.Throws<ProtocolException>(() => MessageSerializer.Deserialize(good.Concat(new byte[] { 0 }).ToArray()));
        Assert.Throws<ProtocolException>(() => MessageSerializer.Deserialize(new byte[] { (byte)ProtocolInfo.Version, 0, 200 }));
        Assert.Throws<ProtocolException>(() => MessageSerializer.Deserialize(Array.Empty<byte>()));
    }

    [Fact]
    public void Long_strings_are_truncated_to_valid_utf8()
    {
        var name = new string('é', 200); // 400 UTF-8 bytes
        var back = (Hello)MessageSerializer.Deserialize(MessageSerializer.Serialize(new Hello(1, name)));
        Assert.Equal(127, back.Name.Length);
        Assert.All(back.Name, c => Assert.Equal('é', c));
    }

    public static TheoryData<RelayFrame> AllRelayFrames() => new()
    {
        new CreateRoom(),
        new RoomCreated("ABC234"),
        new JoinRoom("ABC234"),
        new JoinAccepted(5),
        new Rejected("nope"),
        new PeerJoined(5),
        new PeerLeft(5),
        new Kick(5),
        new RoomClosed("Host left"),
    };

    [Theory]
    [MemberData(nameof(AllRelayFrames))]
    public void Every_relay_frame_round_trips(RelayFrame frame)
    {
        Assert.Equal(frame, RelaySerializer.Deserialize(RelaySerializer.Serialize(frame)));
    }

    [Fact]
    public void Relay_data_round_trips_payload()
    {
        var payload = MessageSerializer.Serialize(new KeyPress(KeyCodes.ENTER, 9));
        var back = (RelayData)RelaySerializer.Deserialize(RelaySerializer.Serialize(new RelayData(3, payload)));
        Assert.Equal(3, back.PeerId);
        Assert.Equal(payload, back.Payload);
    }

    [Fact]
    public void Relay_frame_from_other_relay_version_is_rejected()
    {
        var bytes = RelaySerializer.Serialize(new CreateRoom(), version: 99);
        Assert.Throws<ProtocolVersionException>(() => RelaySerializer.Deserialize(bytes));
    }

    [Fact]
    public void Room_codes_are_short_and_unambiguous()
    {
        var code = RoomCode.Generate();
        Assert.Equal(6, code.Length);
        Assert.All(code, c => Assert.Contains(c, RoomCode.Alphabet));
        Assert.Equal("ABC234", RoomCode.Normalize("  abc234 "));
    }

    [Fact]
    public void KeyCodes_match_inputhook_Key_enum_exactly()
    {
        var upstream = Enum.GetValues<Inputs.Key>().ToDictionary(k => (int)k, k => k.ToString());

        Assert.Equal(upstream.Count, KeyCodes.Names.Count);
        foreach (var (value, name) in upstream)
        {
            Assert.True(KeyCodes.Names.TryGetValue(value, out var ours), $"missing {name}={value}");
            Assert.Equal(name, ours);
        }
    }
}

public class SequenceReorderBufferTests
{
    [Fact]
    public void Releases_in_order_and_drops_duplicates()
    {
        var buffer = new SequenceReorderBuffer<string>();
        var released = new List<string>();

        Assert.Equal(SequenceResult.Buffered, buffer.Push(2, "b", released));
        Assert.Equal(SequenceResult.Buffered, buffer.Push(3, "c", released));
        Assert.Equal(SequenceResult.Duplicate, buffer.Push(3, "c", released));
        Assert.Empty(released);
        Assert.Equal(SequenceResult.Delivered, buffer.Push(1, "a", released));
        Assert.Equal(new[] { "a", "b", "c" }, released);
        Assert.Equal(SequenceResult.Duplicate, buffer.Push(1, "a", released));
        Assert.Equal(SequenceResult.Duplicate, buffer.Push(2, "b", released));
        Assert.Equal(4u, buffer.NextExpected);
    }

    [Fact]
    public void Gives_up_on_a_gap_that_never_fills()
    {
        var buffer = new SequenceReorderBuffer<int>(maxPending: 2);
        var released = new List<int>();

        buffer.Push(2, 2, released);
        buffer.Push(3, 3, released);
        Assert.Empty(released);
        Assert.Equal(SequenceResult.Delivered, buffer.Push(4, 4, released)); // 3 pending > 2: skip seq 1
        Assert.Equal(new[] { 2, 3, 4 }, released);
        Assert.Equal(1, buffer.Skipped);
        Assert.Equal(SequenceResult.Duplicate, buffer.Push(1, 1, released));
    }
}
