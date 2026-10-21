using System;
using System.Threading;
using System.Threading.Tasks;
using P5RCoop.Protocol.Transport;

namespace P5RCoop.Protocol.Relay;

/// <summary>The relay refused to create or join a room.</summary>
public sealed class RelayRejectedException : Exception
{
    public RelayRejectedException(string reason) : base(reason) { }
}

/// <summary>
/// Client side of the relay handshake plus typed send/receive, shared by the host mod and the friend app.
/// </summary>
public sealed class RelayClient : IDisposable
{
    private RelayClient(IConnection connection)
    {
        Connection = connection;
    }

    public IConnection Connection { get; }

    /// <summary>Join code (host) or the code that was joined (friend).</summary>
    public string Code { get; private set; } = string.Empty;

    /// <summary>Our peer id within the room (friends only; the host has none).</summary>
    public byte PeerId { get; private set; }

    public static async Task<RelayClient> CreateRoomAsync(IConnection connection, CancellationToken ct = default)
    {
        var client = new RelayClient(connection);
        await client.SendAsync(new CreateRoom(), ct).ConfigureAwait(false);
        switch (await client.ReceiveAsync(ct).ConfigureAwait(false))
        {
            case RoomCreated created:
                client.Code = created.Code;
                return client;
            case Rejected rejected:
                connection.Close();
                throw new RelayRejectedException(rejected.Reason);
            case null:
                throw new RelayRejectedException("Relay closed the connection");
            case var other:
                connection.Close();
                throw new ProtocolException($"Unexpected relay frame {other.Kind}");
        }
    }

    public static async Task<RelayClient> JoinRoomAsync(IConnection connection, string code, CancellationToken ct = default)
    {
        var client = new RelayClient(connection);
        code = RoomCode.Normalize(code);
        await client.SendAsync(new JoinRoom(code), ct).ConfigureAwait(false);
        switch (await client.ReceiveAsync(ct).ConfigureAwait(false))
        {
            case JoinAccepted accepted:
                client.Code = code;
                client.PeerId = accepted.PeerId;
                return client;
            case Rejected rejected:
                connection.Close();
                throw new RelayRejectedException(rejected.Reason);
            case null:
                throw new RelayRejectedException("Relay closed the connection");
            case var other:
                connection.Close();
                throw new ProtocolException($"Unexpected relay frame {other.Kind}");
        }
    }

    public ValueTask SendAsync(RelayFrame frame, CancellationToken ct = default) =>
        Connection.SendAsync(RelaySerializer.Serialize(frame), ct);

    /// <summary>Sends a game message to <paramref name="peerId"/> (host) or to the host (friend).</summary>
    public ValueTask SendMessageAsync(Message message, byte peerId = RelayData.Broadcast, CancellationToken ct = default) =>
        SendAsync(new RelayData(peerId, MessageSerializer.Serialize(message)), ct);

    /// <summary>Next relay frame, or null when the connection closed.</summary>
    public async ValueTask<RelayFrame?> ReceiveAsync(CancellationToken ct = default)
    {
        var data = await Connection.ReceiveAsync(ct).ConfigureAwait(false);
        return data is null ? null : RelaySerializer.Deserialize(data);
    }

    public void Dispose() => Connection.Close();
}
