using p5rcoop.battle.Net;
using p5rcoop.battle.Net.Fakes;
using P5RCoop.Protocol;

namespace P5RCoop.Tests;

/// <summary>HostSession against fakes, with no network: every step is synchronous and deterministic.</summary>
public class HostSessionTests
{
    private const byte Joker = 0, Ryuji = 1, Ann = 2;
    private const byte RyujiPeer = 10, AnnPeer = 11;

    private readonly ManualTurnSource _turns = new();
    private readonly FakeInputHook _game = new();
    private readonly RecordingLink _link = new();
    private readonly List<string> _log = new();
    private readonly HostSession _session;

    public HostSessionTests()
    {
        _session = new HostSession(new HostSessionOptions
        {
            HostName = "Host",
            Seats = new[] { new SeatDefinition(Ryuji, "Ryuji"), new SeatDefinition(Ann, "Ann") },
            Log = _log.Add,
        }, _turns, _game, _link);
    }

    private void Join(byte peer, string name, byte? slot = null)
    {
        _session.OnPeerJoined(peer);
        _session.OnFrame(peer, MessageSerializer.Serialize(new Hello(ProtocolInfo.Version, name)));
        if (slot is byte s)
            _session.OnMessage(peer, new SeatClaim(s));
    }

    private void Key(byte peer, int key, uint seq) =>
        _session.OnFrame(peer, MessageSerializer.Serialize(new KeyPress(key, seq)));

    [Fact]
    public void Hello_is_answered_with_welcome_seats_and_turn()
    {
        _turns.SetTurn(Joker);
        Join(RyujiPeer, "  Ryu\u0007ji fan  ");

        var welcome = Assert.Single(_link.SentTo<Welcome>(RyujiPeer));
        Assert.True(welcome.Accepted);
        Assert.Equal(RyujiPeer, welcome.PlayerId);
        Assert.Equal("Ryuji fan", _session.Players[RyujiPeer]);
        Assert.Equal(2, Assert.Single(_link.SentTo<SeatAssignments>(RyujiPeer)).Seats.Count);
        Assert.Equal(Joker, Assert.Single(_link.SentTo<TurnChanged>(RyujiPeer)).Slot);
    }

    [Fact]
    public void Hello_with_other_protocol_version_is_rejected_and_kicked()
    {
        _session.OnPeerJoined(RyujiPeer);
        _session.OnMessage(RyujiPeer, new Hello((ushort)(ProtocolInfo.Version + 1), "future"));

        var welcome = Assert.Single(_link.SentTo<Welcome>(RyujiPeer));
        Assert.False(welcome.Accepted);
        Assert.Contains("mismatch", welcome.Reason);
        Assert.Contains(RyujiPeer, _link.Kicked);
        Assert.Empty(_session.Players);
    }

    [Fact]
    public void Frame_with_other_protocol_version_is_rejected_and_kicked()
    {
        _session.OnPeerJoined(RyujiPeer);
        _session.OnFrame(RyujiPeer, MessageSerializer.Serialize(new Hello(9, "future"), version: 9));

        Assert.False(Assert.Single(_link.SentTo<Welcome>(RyujiPeer)).Accepted);
        Assert.Contains(RyujiPeer, _link.Kicked);
    }

    [Fact]
    public void Messages_before_hello_are_ignored()
    {
        _session.OnPeerJoined(RyujiPeer);
        _session.OnMessage(RyujiPeer, new SeatClaim(Ryuji));
        Assert.All(_session.Seats, s => Assert.True(s.IsFree));
        Assert.Empty(_link.Sent);
    }

    [Fact]
    public void Seat_claim_conflicts()
    {
        Join(RyujiPeer, "R", Ryuji);
        Join(AnnPeer, "A");

        // Taken by someone else.
        _session.OnMessage(AnnPeer, new SeatClaim(Ryuji));
        var refused = _link.SentTo<SeatClaimResult>(AnnPeer).Last();
        Assert.False(refused.Accepted);
        Assert.Contains("taken by R", refused.Reason);

        // Not a claimable seat (Joker is the host's).
        _session.OnMessage(AnnPeer, new SeatClaim(Joker));
        Assert.False(_link.SentTo<SeatClaimResult>(AnnPeer).Last().Accepted);

        // Re-claiming your own seat is fine.
        _session.OnMessage(RyujiPeer, new SeatClaim(Ryuji));
        Assert.True(_link.SentTo<SeatClaimResult>(RyujiPeer).Last().Accepted);

        // Claiming another seat moves you; the old one becomes free and can be taken.
        _session.OnMessage(RyujiPeer, new SeatClaim(Ann));
        _session.OnMessage(AnnPeer, new SeatClaim(Ryuji));
        Assert.True(_link.SentTo<SeatClaimResult>(AnnPeer).Last().Accepted);

        var seats = _session.Seats.ToDictionary(s => s.Slot);
        Assert.Equal(AnnPeer, seats[Ryuji].OwnerId);
        Assert.Equal(RyujiPeer, seats[Ann].OwnerId);
        var table = Assert.IsType<SeatAssignments>(_link.Broadcasts.Last());
        Assert.Equal(seats.Values.OrderBy(s => s.Slot), table.Seats.OrderBy(s => s.Slot));
    }

    [Fact]
    public void Turn_changes_are_broadcast()
    {
        _turns.SetTurn(Joker);
        _turns.SetTurn(Ryuji);
        _turns.SetTurn(Ryuji); // no change, no message
        _turns.EndBattle();

        Assert.Equal(new byte?[] { Joker, Ryuji, null },
            _link.Broadcasts.OfType<TurnChanged>().Select(t => t.Slot).ToArray());
    }

    [Fact]
    public void Host_turn_passes_host_keys_and_drops_friend_keys()
    {
        Join(RyujiPeer, "R", Ryuji);
        _turns.SetTurn(Joker);

        Key(RyujiPeer, KeyCodes.ENTER, 1);
        Assert.Equal(0, _game.QueuedCount);
        Assert.Equal(new[] { KeyCodes.W }, _game.Poll(KeyCodes.W));
        Assert.Equal(1, _session.DroppedOffTurnKeys);
        Assert.Equal(0, _session.InjectedKeys);
    }

    [Fact]
    public void No_battle_passes_host_keys_and_drops_friend_keys()
    {
        Join(RyujiPeer, "R", Ryuji);
        Key(RyujiPeer, KeyCodes.ENTER, 1);

        Assert.Equal(new[] { KeyCodes.ESCAPE }, _game.Poll(KeyCodes.ESCAPE));
        Assert.Equal(1, _session.DroppedOffTurnKeys);
    }

    [Fact]
    public void Unclaimed_friend_slot_is_played_by_host()
    {
        Join(RyujiPeer, "R", Ryuji);
        _turns.SetTurn(Ann); // nobody owns Ann

        Key(RyujiPeer, KeyCodes.ENTER, 1);
        Assert.Equal(new[] { KeyCodes.D }, _game.Poll(KeyCodes.D));
        Assert.Null(_session.ActingPeer);
    }

    [Fact]
    public void Friend_turn_blocks_host_keys_and_injects_friend_keys()
    {
        Join(RyujiPeer, "R", Ryuji);
        _turns.SetTurn(Ryuji);

        Key(RyujiPeer, KeyCodes.DOWN, 1);
        Key(RyujiPeer, KeyCodes.ENTER, 2);

        Assert.Equal(new[] { KeyCodes.DOWN, KeyCodes.ENTER }, _game.Poll(KeyCodes.W, KeyCodes.ESCAPE));
        Assert.Equal(2, _session.BlockedHostKeys);
        // Host keeps mashing with nothing from the friend: still blocked.
        Assert.Empty(_game.Poll(KeyCodes.ENTER));
        Assert.Equal(2, _session.InjectedKeys);
    }

    [Fact]
    public void Friend_keys_arrive_with_no_host_input()
    {
        Join(RyujiPeer, "R", Ryuji);
        _turns.SetTurn(Ryuji);
        Key(RyujiPeer, KeyCodes.ENTER, 1);

        Assert.Equal(new[] { KeyCodes.ENTER }, _game.Poll());
    }

    [Fact]
    public void Other_friends_keys_are_dropped_during_a_friends_turn()
    {
        Join(RyujiPeer, "R", Ryuji);
        Join(AnnPeer, "A", Ann);
        _turns.SetTurn(Ryuji);

        Key(AnnPeer, KeyCodes.ENTER, 1);
        Key(RyujiPeer, KeyCodes.UP, 1);

        Assert.Equal(new[] { KeyCodes.UP }, _game.Poll());
        Assert.Equal(1, _session.DroppedOffTurnKeys);
    }

    [Fact]
    public void Keys_are_injected_in_sequence_order_without_duplicates()
    {
        Join(RyujiPeer, "R", Ryuji);
        _turns.SetTurn(Ryuji);

        Key(RyujiPeer, KeyCodes.ENTER, 3);
        Key(RyujiPeer, KeyCodes.DOWN, 2);
        Key(RyujiPeer, KeyCodes.DOWN, 2); // duplicate while buffered
        Assert.Equal(0, _game.QueuedCount); // waiting for #1
        Key(RyujiPeer, KeyCodes.DOWN, 1);
        Key(RyujiPeer, KeyCodes.DOWN, 1); // duplicate after delivery
        Key(RyujiPeer, KeyCodes.ENTER, 3); // duplicate after delivery

        Assert.Equal(new[] { KeyCodes.DOWN, KeyCodes.DOWN, KeyCodes.ENTER }, _game.Poll());
        Assert.Equal(3, _session.DuplicateKeys);
    }

    [Fact]
    public void Keys_left_over_from_a_finished_turn_never_reach_the_next_turn()
    {
        Join(RyujiPeer, "R", Ryuji);
        Join(AnnPeer, "A", Ann);
        _turns.SetTurn(Ryuji);
        Key(RyujiPeer, KeyCodes.ENTER, 1); // queued in the game, not polled yet

        _turns.SetTurn(Joker);
        Assert.Equal(new[] { KeyCodes.S }, _game.Poll(KeyCodes.S)); // stale ENTER stripped, host key kept

        _turns.SetTurn(Ryuji);
        Key(RyujiPeer, KeyCodes.UP, 2);
        _turns.SetTurn(Ann); // Ryuji's UP is now stale during Ann's turn
        Key(AnnPeer, KeyCodes.DOWN, 1);
        Assert.Equal(new[] { KeyCodes.DOWN }, _game.Poll(KeyCodes.W));
    }

    [Fact]
    public void Friend_disconnect_mid_turn_falls_back_to_host()
    {
        Join(RyujiPeer, "R", Ryuji);
        _turns.SetTurn(Ryuji);
        Key(RyujiPeer, KeyCodes.DOWN, 1);
        Assert.Equal(RyujiPeer, _session.ActingPeer);

        _session.OnPeerLeft(RyujiPeer);

        Assert.Null(_session.ActingPeer);
        Assert.Equal(new[] { KeyCodes.ENTER }, _game.Poll(KeyCodes.ENTER)); // pending DOWN stripped
        Assert.All(_session.Seats, s => Assert.True(s.IsFree));
        Assert.True(Assert.IsType<SeatAssignments>(_link.Broadcasts.Last()).Seats.All(s => s.IsFree));
        Assert.Contains(_log, l => l.Contains("falls back to host"));
    }

    [Fact]
    public void Relay_loss_mid_turn_falls_back_to_host()
    {
        Join(RyujiPeer, "R", Ryuji);
        _turns.SetTurn(Ryuji);

        _session.OnLinkLost();

        Assert.Equal(new[] { KeyCodes.ENTER }, _game.Poll(KeyCodes.ENTER));
    }

    [Fact]
    public void Leave_frees_the_seat_and_kicks()
    {
        Join(RyujiPeer, "R", Ryuji);
        _session.OnMessage(RyujiPeer, new Leave("bye"));

        Assert.Contains(RyujiPeer, _link.Kicked);
        Assert.All(_session.Seats, s => Assert.True(s.IsFree));
    }

    [Fact]
    public void Ping_is_answered_with_pong()
    {
        Join(RyujiPeer, "R");
        _session.OnMessage(RyujiPeer, new Ping(5, 1234));
        Assert.Equal(new Pong(5, 1234), Assert.Single(_link.SentTo<Pong>(RyujiPeer)));
    }

    [Fact]
    public void Garbage_frame_kicks_the_peer()
    {
        Join(RyujiPeer, "R", Ryuji);
        _session.OnFrame(RyujiPeer, new byte[] { (byte)ProtocolInfo.Version, 0, 77 });
        Assert.Contains(RyujiPeer, _link.Kicked);
        Assert.Empty(_session.Players);
    }

    [Fact]
    public void Unknown_key_codes_are_not_injected()
    {
        Join(RyujiPeer, "R", Ryuji);
        _turns.SetTurn(Ryuji);
        Key(RyujiPeer, 12345, 1);
        Assert.Equal(0, _game.QueuedCount);
    }

    [Fact]
    public void Dispose_unhooks_input()
    {
        Join(RyujiPeer, "R", Ryuji);
        _turns.SetTurn(Ryuji);
        _session.Dispose();

        Assert.Equal(new[] { KeyCodes.ENTER }, _game.Poll(KeyCodes.ENTER));
        Assert.IsType<Leave>(_link.Broadcasts.Last());
    }

    [Fact]
    public void Seat_config_string_parses()
    {
        var seats = HostSessionOptions.ParseSeats("1=Ryuji, 2=Ann,7");
        Assert.Equal(new[] { new SeatDefinition(1, "Ryuji"), new SeatDefinition(2, "Ann"), new SeatDefinition(7, "Slot 7") }, seats);
        Assert.Throws<FormatException>(() => HostSessionOptions.ParseSeats("x=Ryuji"));
    }
}
