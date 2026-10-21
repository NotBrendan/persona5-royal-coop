using System;
using System.Threading;
using System.Threading.Tasks;

namespace P5RCoop.Protocol.Transport;

/// <summary>
/// A reliable, ordered, message-oriented connection (one call to <see cref="SendAsync"/> = one frame
/// out of <see cref="ReceiveAsync"/> on the other side). Single reader; sends may come from any thread.
/// </summary>
public interface IConnection : IDisposable
{
    /// <summary>Human-readable remote endpoint, for logs only.</summary>
    string RemoteAddress { get; }

    bool IsConnected { get; }

    /// <summary>Sends one frame. After the connection closed this is a silent no-op.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default);

    /// <summary>Next frame, or null once the connection is closed (by either side).</summary>
    ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Closes both directions. Idempotent.</summary>
    void Close();
}

/// <summary>Accepts incoming connections (relay side).</summary>
public interface IListener : IDisposable
{
    /// <summary>Address clients should connect to, e.g. "127.0.0.1:7777" or "mem:relay".</summary>
    string LocalAddress { get; }

    ValueTask<IConnection> AcceptAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates connections. Implementations: <see cref="InMemoryTransport"/> (tests),
/// <see cref="TcpTransport"/> (real network). A future Steam transport (Spacewar app 480,
/// SteamNetworkingSockets relay) would implement this same interface with a Steam ID or lobby
/// id as the address, so relay, host and friend code stay unchanged.
/// </summary>
public interface ITransport
{
    Task<IConnection> ConnectAsync(string address, CancellationToken cancellationToken = default);

    IListener Listen(string address);
}
