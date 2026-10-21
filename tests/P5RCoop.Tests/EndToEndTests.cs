using p5rcoop.battle.Net;
using p5rcoop.battle.Net.Fakes;
using P5RCoop.Friend;
using P5RCoop.Protocol;
using P5RCoop.Protocol.Relay;
using P5RCoop.Protocol.Transport;
using P5RCoop.Relay;

namespace P5RCoop.Tests;

/// <summary>
/// Whole stack: relay server, host mod session (fake game input + manual turn source) and friend clients,
/// all over a real transport implementation.
/// </summary>
public sealed class EndToEndTests : IAsyncLifetime
{
    private const byte Joker = 0, Ryuji = 1, Ann = 2;

    private readonly CancellationTokenSource _cts = new();
    private readonly ManualTurnSource _turns = new();
    private readonly FakeInputHook _game = new();
    private readonly List<string> _hostLog = new();
    private readonly List<IDisposable> _cleanup = new();
    private ITransport _transport = new InMemoryTransport();
    private IListener _listener = null!;
    private CoopHost _host = null!;

    public Task InitializeAsync() => Task.CompletedTask;

    private async Task StartAsync(ITransport transport, string address)
    {
        _transport = transport;
        _listener = transport.Listen(address);
        _ = new RelayServer(_listener).RunAsync(_cts.Token);
        _host = await CoopHost.StartAsync(_transport, _listener.LocalAddress, new HostSessionOptions
        {
            HostName = "Host",
            Seats = HostSessionOptions.ParseSeats("1=Ryuji,2=Ann"),
            Log = line => { lock (_hostLog) _hostLog.Add(line); },
        }, _turns, _game, _cts.Token).WithTimeout();
    }

    private async Task<FriendClient> JoinAsync(string name, RecordingUi ui)
    {
        var connection = await _transport.ConnectAsync(_listener.LocalAddress);
        var friend = await FriendClient.JoinAsync(connection, _host.Code, name, ui, pingInterval: TimeSpan.FromMilliseconds(20)).WithTimeout();
        _cleanup.Add(friend);
        return friend;
    }

    public Task DisposeAsync()
    {
        foreach (var d in _cleanup)
            d.Dispose();
        _host?.Dispose();
        _cts.Cancel();
        _listener?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Polls the fake game, with the host holding <paramref name="hostKeys"/> on every poll, until it has
    /// processed <paramref name="count"/> keys in total.
    /// </summary>
    private async Task PumpUntilSeenAsync(int count, params int[] hostKeys)
    {
        await Wait.UntilAsync(() =>
        {
            _game.Poll(hostKeys);
            return _game.Seen.Count >= count;
        }, $"game to process {count} keys (has {_game.Seen.Count})");
    }

    private async Task SetTurnAsync(byte? slot, params FriendClient[] friends)
    {
        _turns.SetTurn(slot);
        foreach (var f in friends)
            await Wait.UntilAsync(() => f.CurrentSlot == slot, $"friend {f.PlayerId} to see turn {slot}");
    }

    public static TheoryData<string> Transports() => new() { "memory", "tcp" };

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Scripted_battle_host_then_Ryuji_then_Ann_then_host(string kind)
    {
        if (kind == "memory") await StartAsync(new InMemoryTransport(), "mem:relay");
        else await StartAsync(new TcpTransport(), "127.0.0.1:0");

        var ryujiUi = new RecordingUi();
        var annUi = new RecordingUi();
        var ryuji = await JoinAsync("RyujiPlayer", ryujiUi);
        var ann = await JoinAsync("AnnPlayer", annUi);
        Assert.True((await ryuji.ClaimSeatAsync(Ryuji).WithTimeout()).Accepted);
        Assert.True((await ann.ClaimSeatAsync(Ann).WithTimeout()).Accepted);
        await Wait.UntilAsync(() => ryuji.Seats.Count(s => !s.IsFree) == 2, "Ryuji to see both seats taken");
        Assert.Equal(Ryuji, ryuji.MySlot);
        Assert.Equal(Ann, ann.MySlot);

        var ryujiKeys = new ScriptedKeySource();
        var annKeys = new ScriptedKeySource();
        _ = ryuji.RunKeysAsync(ryujiKeys);
        _ = ann.RunKeysAsync(annKeys);

        // 1. Joker (host) acts: host keys pass; friends' keys are dropped with a visible message.
        await SetTurnAsync(Joker, ryuji, ann);
        Assert.False(await ryuji.PressKeyAsync(KeyCodes.ENTER));
        Assert.Contains(ryujiUi.Warnings, w => w.Contains("Dropped ENTER") && w.Contains("turn"));
        Assert.Equal(new[] { KeyCodes.D, KeyCodes.ENTER }, _game.Poll(KeyCodes.D, KeyCodes.ENTER));

        // 2. Ryuji's command menu opens: Ryuji's friend picks Skill -> 2nd skill -> confirm target.
        await SetTurnAsync(Ryuji, ryuji, ann);
        Assert.True(ryuji.IsMyTurn);
        Assert.Contains(ryujiUi.Infos, i => i.Contains("YOUR TURN"));
        ryujiKeys.Press(KeyCodes.Q, KeyCodes.DOWN, KeyCodes.ENTER, KeyCodes.ENTER);
        Assert.False(await ann.PressKeyAsync(KeyCodes.ESCAPE)); // not Ann's turn
        await PumpUntilSeenAsync(2 + 4, KeyCodes.ESCAPE, KeyCodes.W); // host mashes keys; all blocked
        Assert.True(_host.Session.BlockedHostKeys > 0);

        // 3. Ann's turn: host keys are blocked while Ann's friend attacks.
        await SetTurnAsync(Ann, ryuji, ann);
        annKeys.Press(KeyCodes.F, KeyCodes.ENTER);
        await Wait.UntilAsync(() => _host.Session.InjectedKeys == 6, "Ann's keys to reach the host");
        Assert.Equal(new[] { KeyCodes.F, KeyCodes.ENTER }, _game.Poll(KeyCodes.W, KeyCodes.A));

        // 4. Back to the host for the next round.
        await SetTurnAsync(Joker, ryuji, ann);
        Assert.Equal(new[] { KeyCodes.ENTER }, _game.Poll(KeyCodes.ENTER));
        await SetTurnAsync(null, ryuji, ann);

        Assert.Equal(new[]
        {
            KeyCodes.D, KeyCodes.ENTER,                                   // host, Joker's turn
            KeyCodes.Q, KeyCodes.DOWN, KeyCodes.ENTER, KeyCodes.ENTER,    // Ryuji's friend
            KeyCodes.F, KeyCodes.ENTER,                                   // Ann's friend
            KeyCodes.ENTER,                                               // host again
        }, _game.Seen);
        Assert.Equal(0, _host.Session.DuplicateKeys);
        Assert.Equal(4, ryuji.SentKeys);
        Assert.Equal(2, ann.SentKeys);
        Assert.Equal(1, ryuji.DroppedKeys);
        Assert.Equal(1, ann.DroppedKeys);
        await Wait.UntilAsync(() => ryuji.LastRoundTrip is not null, "a ping round trip");
    }

    [Fact]
    public async Task Friend_disconnect_mid_turn_falls_back_to_host()
    {
        await StartAsync(new InMemoryTransport(), "mem:relay");
        var ui = new RecordingUi();
        var ryuji = await JoinAsync("RyujiPlayer", ui);
        await ryuji.ClaimSeatAsync(Ryuji).WithTimeout();
        await SetTurnAsync(Ryuji, ryuji);

        Assert.True(await ryuji.PressKeyAsync(KeyCodes.DOWN));
        await PumpUntilSeenAsync(1);
        Assert.Empty(_game.Poll(KeyCodes.ENTER)); // host blocked during Ryuji's turn

        ryuji.Dispose(); // network drop, no Leave
        await Wait.UntilAsync(() => _host.Session.ActingPeer is null, "host to take Ryuji's turn back");

        Assert.Equal(new[] { KeyCodes.ENTER }, _game.Poll(KeyCodes.ENTER));
        Assert.All(_host.Session.Seats, s => Assert.True(s.IsFree));

        // The seat is claimable again by someone else.
        var other = await JoinAsync("Other", new RecordingUi());
        Assert.True((await other.ClaimSeatAsync(Ryuji).WithTimeout()).Accepted);
    }

    [Fact]
    public async Task Seat_conflict_between_friends()
    {
        await StartAsync(new InMemoryTransport(), "mem:relay");
        var ui = new RecordingUi();
        var first = await JoinAsync("First", new RecordingUi());
        var second = await JoinAsync("Second", ui);

        Assert.True((await first.ClaimSeatAsync(Ryuji).WithTimeout()).Accepted);
        var refused = await second.ClaimSeatAsync(Ryuji).WithTimeout();
        Assert.False(refused.Accepted);
        Assert.Contains(ui.Warnings, w => w.Contains("refused") && w.Contains("First"));
        Assert.Equal(Ryuji, second.FindSeat("ryuji")!.Slot);
    }

    [Fact]
    public async Task Friend_with_other_protocol_version_gets_a_clear_error()
    {
        await StartAsync(new InMemoryTransport(), "mem:relay");

        // An "old" friend: speaks the relay protocol but sends a Hello framed with protocol v0.
        var relay = await RelayClient.JoinRoomAsync(await _transport.ConnectAsync(_listener.LocalAddress), _host.Code).WithTimeout();
        await relay.SendAsync(new RelayData(0, MessageSerializer.Serialize(new Hello(0, "old"), version: 0)));

        var reply = Assert.IsType<RelayData>(await relay.ReceiveAsync().AsTask().WithTimeout());
        var welcome = Assert.IsType<Welcome>(MessageSerializer.Deserialize(reply.Payload));
        Assert.False(welcome.Accepted);
        Assert.Contains("mismatch", welcome.Reason);
        Assert.Null(await relay.ReceiveAsync().AsTask().WithTimeout()); // kicked
    }

    [Fact]
    public async Task Friend_app_reports_a_host_on_another_protocol_version()
    {
        // Fake host on a "newer" protocol: answers every Hello with a v2-framed Welcome.
        _listener = _transport.Listen("mem:relay");
        _ = new RelayServer(_listener).RunAsync(_cts.Token);
        var fakeHost = await RelayClient.CreateRoomAsync(await _transport.ConnectAsync(_listener.LocalAddress)).WithTimeout();
        _ = Task.Run(async () =>
        {
            while (await fakeHost.ReceiveAsync(_cts.Token) is { } frame)
            {
                if (frame is RelayData data)
                    await fakeHost.SendAsync(new RelayData(data.PeerId,
                        MessageSerializer.Serialize(new Welcome(true, "", data.PeerId, "Future"), version: ProtocolInfo.Version + 1)));
            }
        });

        var connection = await _transport.ConnectAsync(_listener.LocalAddress);
        var e = await Assert.ThrowsAsync<JoinFailedException>(() =>
            FriendClient.JoinAsync(connection, fakeHost.Code, "me", new RecordingUi()).WithTimeout());
        Assert.Contains($"host speaks protocol v{ProtocolInfo.Version + 1}", e.Message);
        fakeHost.Dispose();
    }

    [Fact]
    public async Task Wrong_join_code_is_reported()
    {
        await StartAsync(new InMemoryTransport(), "mem:relay");
        var connection = await _transport.ConnectAsync(_listener.LocalAddress);
        var e = await Assert.ThrowsAsync<JoinFailedException>(() =>
            FriendClient.JoinAsync(connection, "NOPE22", "me", new RecordingUi()));
        Assert.Contains("No room", e.Message);
    }

    [Fact]
    public async Task Host_leaving_disconnects_friends_and_off_turn_keys_are_dropped()
    {
        await StartAsync(new InMemoryTransport(), "mem:relay");
        var ui = new RecordingUi();
        var ryuji = await JoinAsync("RyujiPlayer", ui);

        Assert.False(await ryuji.PressKeyAsync(KeyCodes.ENTER));
        Assert.Contains(ui.Warnings, w => w.Contains("no seat"));

        _host.Dispose();
        await ryuji.Completion.WaitAsync(Wait.Timeout);
        Assert.NotNull(ryuji.DisconnectReason);
        Assert.Contains(ui.Warnings, w => w.StartsWith("Disconnected"));
    }
}
