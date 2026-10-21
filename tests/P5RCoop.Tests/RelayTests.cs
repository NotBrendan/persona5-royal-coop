using P5RCoop.Protocol;
using P5RCoop.Protocol.Relay;
using P5RCoop.Protocol.Transport;
using P5RCoop.Relay;

namespace P5RCoop.Tests;

/// <summary>The relay with raw relay clients: it only forwards and never parses game payloads.</summary>
public sealed class RelayTests : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _log = new();
    private ITransport _transport = null!;
    private IListener _listener = null!;
    private RelayServer _relay = null!;

    private void Start(ITransport transport, string address)
    {
        _transport = transport;
        _listener = transport.Listen(address);
        _relay = new RelayServer(_listener, line => { lock (_log) _log.Add(line); });
        _ = _relay.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Dispose();
    }

    private async Task<RelayClient> Connect(Func<IConnection, Task<RelayClient>> handshake) =>
        await handshake(await _transport.ConnectAsync(_listener.LocalAddress)).WithTimeout();

    private static async Task<RelayFrame?> Next(RelayClient client) => await client.ReceiveAsync().AsTask().WithTimeout();

    public static TheoryData<string> Transports() => new() { "memory", "tcp" };

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Relay_forwards_between_host_and_two_friends(string kind)
    {
        if (kind == "memory") Start(new InMemoryTransport(), "mem:relay");
        else Start(new TcpTransport(), "127.0.0.1:0");

        using var host = await Connect(c => RelayClient.CreateRoomAsync(c));
        Assert.Equal(RoomCode.Length, host.Code.Length);

        using var ryuji = await Connect(c => RelayClient.JoinRoomAsync(c, host.Code.ToLowerInvariant()));
        Assert.Equal(new PeerJoined(ryuji.PeerId), await Next(host));
        using var ann = await Connect(c => RelayClient.JoinRoomAsync(c, host.Code));
        Assert.Equal(new PeerJoined(ann.PeerId), await Next(host));
        Assert.NotEqual(ryuji.PeerId, ann.PeerId);
        Assert.Equal(2, _relay.FriendCount(host.Code));

        // Friend -> host, tagged with the sender's peer id.
        await ryuji.SendMessageAsync(new KeyPress(KeyCodes.ENTER, 1));
        await ann.SendMessageAsync(new KeyPress(KeyCodes.UP, 1));
        // Two connections: no ordering between them, so match by sender.
        var received = new[] { Assert.IsType<RelayData>(await Next(host)), Assert.IsType<RelayData>(await Next(host)) }
            .ToDictionary(d => d.PeerId, d => MessageSerializer.Deserialize(d.Payload));
        Assert.Equal(new KeyPress(KeyCodes.ENTER, 1), received[ryuji.PeerId]);
        Assert.Equal(new KeyPress(KeyCodes.UP, 1), received[ann.PeerId]);

        // Host -> everyone.
        await host.SendMessageAsync(new TurnChanged(1));
        Assert.Equal(new TurnChanged(1), Payload(await Next(ryuji)));
        Assert.Equal(new TurnChanged(1), Payload(await Next(ann)));

        // Host -> one friend only.
        await host.SendMessageAsync(new Pong(1, 2), ann.PeerId);
        await host.SendMessageAsync(new TurnChanged(null));
        Assert.Equal(new Pong(1, 2), Payload(await Next(ann)));
        Assert.Equal(new TurnChanged(null), Payload(await Next(ryuji))); // Ryuji never saw the Pong
        Assert.Equal(new TurnChanged(null), Payload(await Next(ann)));

        // Payloads are opaque: even an unparseable one is forwarded.
        await ryuji.SendAsync(new RelayData(0, new byte[] { 0xDE, 0xAD }));
        Assert.Equal(new byte[] { 0xDE, 0xAD }, Assert.IsType<RelayData>(await Next(host)).Payload);

        // Friend leaves -> host told.
        ann.Dispose();
        Assert.Equal(new PeerLeft(ann.PeerId), await Next(host));

        // Host kicks -> friend disconnected, host told.
        await host.SendAsync(new Kick(ryuji.PeerId));
        Assert.Null(await Next(ryuji));
        Assert.Equal(new PeerLeft(ryuji.PeerId), await Next(host));
        Assert.Equal(0, _relay.FriendCount(host.Code));
    }

    [Fact]
    public async Task Unknown_code_is_rejected()
    {
        Start(new InMemoryTransport(), "mem:relay");
        var e = await Assert.ThrowsAsync<RelayRejectedException>(() => Connect(c => RelayClient.JoinRoomAsync(c, "ZZZZZZ")));
        Assert.Contains("ZZZZZZ", e.Message);
    }

    [Fact]
    public async Task Wrong_relay_version_is_rejected()
    {
        Start(new InMemoryTransport(), "mem:relay");
        var connection = await _transport.ConnectAsync(_listener.LocalAddress);
        await connection.SendAsync(RelaySerializer.Serialize(new CreateRoom(), version: 42));

        var reply = RelaySerializer.Deserialize((await connection.ReceiveAsync().AsTask().WithTimeout())!);
        Assert.Contains("v1", Assert.IsType<Rejected>(reply).Reason);
        Assert.Null(await connection.ReceiveAsync().AsTask().WithTimeout());
    }

    [Fact]
    public async Task Host_leaving_closes_the_room_for_friends()
    {
        Start(new InMemoryTransport(), "mem:relay");
        var host = await Connect(c => RelayClient.CreateRoomAsync(c));
        using var friend = await Connect(c => RelayClient.JoinRoomAsync(c, host.Code));
        await Next(host); // PeerJoined

        host.Dispose();

        Assert.IsType<RoomClosed>(await Next(friend));
        Assert.Null(await Next(friend));
        await Wait.UntilAsync(() => _relay.RoomCount == 0, "room removed");
        await Assert.ThrowsAsync<RelayRejectedException>(() => Connect(c => RelayClient.JoinRoomAsync(c, host.Code)));
    }

    [Fact]
    public async Task Silent_connection_is_dropped_after_handshake_timeout()
    {
        _transport = new InMemoryTransport();
        _listener = _transport.Listen("mem:relay");
        _relay = new RelayServer(_listener, handshakeTimeout: TimeSpan.FromMilliseconds(100));
        _ = _relay.RunAsync(_cts.Token);

        var connection = await _transport.ConnectAsync(_listener.LocalAddress);
        Assert.Null(await connection.ReceiveAsync().AsTask().WithTimeout());
    }

    private static Message Payload(RelayFrame? frame) =>
        MessageSerializer.Deserialize(Assert.IsType<RelayData>(frame).Payload);
}
