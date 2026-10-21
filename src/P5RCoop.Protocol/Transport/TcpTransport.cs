using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace P5RCoop.Protocol.Transport;

/// <summary>
/// TCP transport. Each frame is prefixed with a little-endian u16 length; frames larger than
/// <see cref="ProtocolInfo.MaxFrameSize"/> close the connection. Nagle is disabled because frames are tiny
/// and latency matters for key presses. Addresses are "host:port".
/// </summary>
public sealed class TcpTransport : ITransport
{
    public async Task<IConnection> ConnectAsync(string address, CancellationToken cancellationToken = default)
    {
        var (host, port) = ParseAddress(address);
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        return new Connection(client, address);
    }

    public IListener Listen(string address)
    {
        var (host, port) = ParseAddress(address);
        var ip = host is "" or "*" or "0.0.0.0" ? IPAddress.Any : IPAddress.Parse(host);
        var listener = new TcpListener(ip, port);
        listener.Start();
        return new Listener(listener);
    }

    /// <summary>Parses "host:port" (or just ":port"/"port" for any interface).</summary>
    public static (string Host, int Port) ParseAddress(string address)
    {
        var colon = address.LastIndexOf(':');
        var host = colon < 0 ? "" : address[..colon];
        var portText = colon < 0 ? address : address[(colon + 1)..];
        if (!int.TryParse(portText, out var port) || port is < 0 or > 65535)
            throw new FormatException($"Bad address '{address}', expected host:port");
        return (host, port);
    }

    private sealed class Listener : IListener
    {
        private readonly TcpListener _listener;

        public Listener(TcpListener listener)
        {
            _listener = listener;
            var ep = (IPEndPoint)listener.LocalEndpoint;
            var host = ep.Address.Equals(IPAddress.Any) ? "127.0.0.1" : ep.Address.ToString();
            LocalAddress = $"{host}:{ep.Port}";
        }

        public string LocalAddress { get; }

        public async ValueTask<IConnection> AcceptAsync(CancellationToken cancellationToken = default)
        {
            var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            client.NoDelay = true;
            return new Connection(client, client.Client.RemoteEndPoint?.ToString() ?? "?");
        }

        public void Dispose() => _listener.Stop();
    }

    private sealed class Connection : IConnection
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly byte[] _header = new byte[2];
        private volatile bool _closed;

        public Connection(TcpClient client, string remote)
        {
            _client = client;
            _stream = client.GetStream();
            RemoteAddress = remote;
        }

        public string RemoteAddress { get; }
        public bool IsConnected => !_closed && _client.Connected;

        public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
        {
            if (frame.Length > ProtocolInfo.MaxFrameSize)
                throw new ArgumentException($"Frame too large ({frame.Length} bytes)");
            if (_closed)
                return;
            var buffer = new byte[2 + frame.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(buffer, (ushort)frame.Length);
            frame.CopyTo(buffer.AsMemory(2));
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
            {
                Close();
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
        {
            if (_closed)
                return null;
            try
            {
                await _stream.ReadExactlyAsync(_header, cancellationToken).ConfigureAwait(false);
                int length = BinaryPrimitives.ReadUInt16LittleEndian(_header);
                if (length > ProtocolInfo.MaxFrameSize)
                {
                    Close();
                    return null;
                }
                var frame = new byte[length];
                await _stream.ReadExactlyAsync(frame, cancellationToken).ConfigureAwait(false);
                return frame;
            }
            catch (Exception e) when (e is IOException or EndOfStreamException or ObjectDisposedException or SocketException)
            {
                Close();
                return null;
            }
        }

        public void Close()
        {
            if (_closed)
                return;
            _closed = true;
            try { _client.Client.Shutdown(SocketShutdown.Both); } catch (Exception) { /* already gone */ }
            _client.Dispose();
        }

        public void Dispose() => Close();
    }
}
