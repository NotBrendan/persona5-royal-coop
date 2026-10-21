using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace P5RCoop.Protocol.Transport;

/// <summary>In-process transport for tests. Addresses are arbitrary names, scoped to one instance.</summary>
public sealed class InMemoryTransport : ITransport
{
    private readonly ConcurrentDictionary<string, Listener> _listeners = new();
    private int _nextClient;

    public IListener Listen(string address)
    {
        var listener = new Listener(this, address);
        if (!_listeners.TryAdd(address, listener))
            throw new InvalidOperationException($"Address {address} already in use");
        return listener;
    }

    public Task<IConnection> ConnectAsync(string address, CancellationToken cancellationToken = default)
    {
        if (!_listeners.TryGetValue(address, out var listener))
            throw new InvalidOperationException($"Nothing listening on {address}");
        var clientName = $"mem-client-{Interlocked.Increment(ref _nextClient)}";
        var (client, server) = CreatePair(clientName, address);
        if (!listener.Pending.Writer.TryWrite(server))
            throw new InvalidOperationException($"Listener {address} closed");
        return Task.FromResult<IConnection>(client);
    }

    /// <summary>Two connected ends, without any listener.</summary>
    public static (IConnection A, IConnection B) CreatePair(string nameA = "a", string nameB = "b")
    {
        var aToB = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        var bToA = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        var a = new Connection(nameB, inbox: bToA, outbox: aToB);
        var b = new Connection(nameA, inbox: aToB, outbox: bToA);
        return (a, b);
    }

    private sealed class Listener : IListener
    {
        private readonly InMemoryTransport _owner;

        public Listener(InMemoryTransport owner, string address)
        {
            _owner = owner;
            LocalAddress = address;
        }

        public Channel<IConnection> Pending { get; } = Channel.CreateUnbounded<IConnection>();
        public string LocalAddress { get; }

        public async ValueTask<IConnection> AcceptAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return await Pending.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                throw new ObjectDisposedException(nameof(Listener));
            }
        }

        public void Dispose()
        {
            Pending.Writer.TryComplete();
            _owner._listeners.TryRemove(LocalAddress, out _);
        }
    }

    private sealed class Connection : IConnection
    {
        private readonly Channel<byte[]> _inbox;
        private readonly Channel<byte[]> _outbox;
        private volatile bool _closed;

        public Connection(string remote, Channel<byte[]> inbox, Channel<byte[]> outbox)
        {
            RemoteAddress = remote;
            _inbox = inbox;
            _outbox = outbox;
        }

        public string RemoteAddress { get; }

        public bool IsConnected => !_closed && !_inbox.Reader.Completion.IsCompleted;

        public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
        {
            if (!_closed)
                _outbox.Writer.TryWrite(frame.ToArray());
            return ValueTask.CompletedTask;
        }

        public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
        {
            if (_closed)
                return null;
            try
            {
                return await _inbox.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public void Close()
        {
            _closed = true;
            _outbox.Writer.TryComplete();
            _inbox.Writer.TryComplete();
        }

        public void Dispose() => Close();
    }
}
