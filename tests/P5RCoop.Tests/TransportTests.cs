using P5RCoop.Protocol.Transport;

namespace P5RCoop.Tests;

public class TransportTests
{
    public static TheoryData<string> Transports() => new() { "memory", "tcp" };

    private static (ITransport Transport, IListener Listener) Create(string kind) => kind switch
    {
        "memory" => Make(new InMemoryTransport(), "mem:test"),
        _ => Make(new TcpTransport(), "127.0.0.1:0"),
    };

    private static (ITransport, IListener) Make(ITransport t, string address) => (t, t.Listen(address));

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Frames_arrive_whole_and_in_order(string kind)
    {
        var (transport, listener) = Create(kind);
        using var _ = listener;
        var accept = listener.AcceptAsync().AsTask();
        using var client = await transport.ConnectAsync(listener.LocalAddress);
        using var server = await accept.WithTimeout();

        for (var i = 0; i < 100; i++)
            await client.SendAsync(new byte[] { (byte)i, 1, 2, 3 });
        await client.SendAsync(Array.Empty<byte>());
        await server.SendAsync(new byte[] { 9 });

        for (var i = 0; i < 100; i++)
            Assert.Equal(new byte[] { (byte)i, 1, 2, 3 }, await server.ReceiveAsync().AsTask().WithTimeout());
        Assert.Empty((await server.ReceiveAsync().AsTask().WithTimeout())!);
        Assert.Equal(new byte[] { 9 }, await client.ReceiveAsync().AsTask().WithTimeout());
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Close_is_seen_by_the_other_side(string kind)
    {
        var (transport, listener) = Create(kind);
        using var _ = listener;
        var accept = listener.AcceptAsync().AsTask();
        var client = await transport.ConnectAsync(listener.LocalAddress);
        using var server = await accept.WithTimeout();

        await client.SendAsync(new byte[] { 1 });
        client.Close();

        Assert.Equal(new byte[] { 1 }, await server.ReceiveAsync().AsTask().WithTimeout());
        Assert.Null(await server.ReceiveAsync().AsTask().WithTimeout());
        Assert.False(client.IsConnected);
        await client.SendAsync(new byte[] { 2 }); // no-op, no throw
    }
}
