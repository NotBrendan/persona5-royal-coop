using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using P5RCoop.Protocol;
using P5RCoop.Protocol.Relay;
using P5RCoop.Protocol.Transport;

namespace P5RCoop.Relay;

/// <summary>
/// Dumb forwarder. Everyone connects out to it, so nobody needs port forwarding at home.
/// The host opens a room and gets a short join code; friends join with the code. The relay never
/// looks inside <see cref="RelayData"/> payloads: the host is authoritative for seats, turns and input.
/// </summary>
public sealed class RelayServer
{
    public const int MaxFriendsPerRoom = 16;
    public const int MaxRooms = 1000;

    private readonly IListener _listener;
    private readonly Action<string> _log;
    private readonly TimeSpan _handshakeTimeout;
    private readonly object _gate = new();
    private readonly Dictionary<string, Room> _rooms = new(StringComparer.Ordinal);

    public RelayServer(IListener listener, Action<string>? log = null, TimeSpan? handshakeTimeout = null)
    {
        _listener = listener;
        _log = log ?? (_ => { });
        _handshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(10);
    }

    public int RoomCount
    {
        get { lock (_gate) return _rooms.Count; }
    }

    /// <summary>Number of friends currently in the room, or -1 if no such room.</summary>
    public int FriendCount(string code)
    {
        lock (_gate)
            return _rooms.TryGetValue(RoomCode.Normalize(code), out var room) ? room.Friends.Count : -1;
    }

    /// <summary>Accepts connections until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _log($"relay listening on {_listener.LocalAddress}");
        while (!ct.IsCancellationRequested)
        {
            IConnection connection;
            try
            {
                connection = await _listener.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            _ = Task.Run(() => HandleConnectionAsync(connection, ct), CancellationToken.None);
        }
    }

    private async Task HandleConnectionAsync(IConnection connection, CancellationToken ct)
    {
        try
        {
            RelayFrame? first;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(_handshakeTimeout);
                var data = await connection.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                if (data is null)
                    return;
                first = RelaySerializer.Deserialize(data);
            }

            switch (first)
            {
                case CreateRoom:
                    await RunHostAsync(connection, ct).ConfigureAwait(false);
                    break;
                case JoinRoom join:
                    await RunFriendAsync(connection, join.Code, ct).ConfigureAwait(false);
                    break;
                default:
                    await RejectAsync(connection, "Expected CreateRoom or JoinRoom").ConfigureAwait(false);
                    break;
            }
        }
        catch (ProtocolVersionException e)
        {
            await RejectAsync(connection, $"Relay protocol v{e.LocalVersion} required (you sent v{e.RemoteVersion})").ConfigureAwait(false);
        }
        catch (ProtocolException e)
        {
            _log($"{connection.RemoteAddress}: bad frame: {e.Message}");
        }
        catch (OperationCanceledException)
        {
            // Handshake timeout or shutdown.
        }
        finally
        {
            connection.Close();
        }
    }

    private static async Task RejectAsync(IConnection connection, string reason)
    {
        await connection.SendAsync(RelaySerializer.Serialize(new Rejected(reason))).ConfigureAwait(false);
        connection.Close();
    }

    private async Task RunHostAsync(IConnection host, CancellationToken ct)
    {
        Room? room = null;
        lock (_gate)
        {
            if (_rooms.Count < MaxRooms)
            {
                string code;
                do code = RoomCode.Generate(); while (_rooms.ContainsKey(code));
                room = new Room(code, host);
                _rooms.Add(code, room);
            }
        }
        if (room is null)
        {
            await RejectAsync(host, "Relay is full").ConfigureAwait(false);
            return;
        }

        _log($"room {room.Code} opened by {host.RemoteAddress}");
        await room.SendToHostAsync(new RoomCreated(room.Code)).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var data = await host.ReceiveAsync(ct).ConfigureAwait(false);
                if (data is null)
                    break;
                switch (RelaySerializer.Deserialize(data))
                {
                    case RelayData d when d.PeerId == RelayData.Broadcast:
                        foreach (var friend in room.SnapshotFriends())
                            await friend.SendAsync(RelaySerializer.Serialize(new RelayData(0, d.Payload))).ConfigureAwait(false);
                        break;
                    case RelayData d:
                        if (room.TryGetFriend(d.PeerId, out var target))
                            await target.SendAsync(RelaySerializer.Serialize(new RelayData(0, d.Payload))).ConfigureAwait(false);
                        break;
                    case Kick k:
                        if (room.TryGetFriend(k.PeerId, out var kicked))
                        {
                            _log($"room {room.Code}: host kicked peer {k.PeerId}");
                            kicked.Close();
                        }
                        break;
                    case var other:
                        _log($"room {room.Code}: ignoring {other.Kind} from host");
                        break;
                }
            }
        }
        finally
        {
            lock (_gate)
                _rooms.Remove(room.Code);
            foreach (var friend in room.SnapshotFriends())
            {
                await friend.SendAsync(RelaySerializer.Serialize(new RoomClosed("Host left"))).ConfigureAwait(false);
                friend.Close();
            }
            _log($"room {room.Code} closed");
        }
    }

    private async Task RunFriendAsync(IConnection friend, string code, CancellationToken ct)
    {
        code = RoomCode.Normalize(code);
        Room? room;
        lock (_gate)
            _rooms.TryGetValue(code, out room);
        if (room is null)
        {
            await RejectAsync(friend, $"No room with code {code}").ConfigureAwait(false);
            return;
        }
        if (!room.TryAddFriend(friend, out var peerId))
        {
            await RejectAsync(friend, "Room is full").ConfigureAwait(false);
            return;
        }

        _log($"room {room.Code}: peer {peerId} joined from {friend.RemoteAddress}");
        await friend.SendAsync(RelaySerializer.Serialize(new JoinAccepted(peerId))).ConfigureAwait(false);
        await room.SendToHostAsync(new PeerJoined(peerId)).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var data = await friend.ReceiveAsync(ct).ConfigureAwait(false);
                if (data is null)
                    break;
                if (RelaySerializer.Deserialize(data) is RelayData d)
                    await room.SendToHostAsync(new RelayData(peerId, d.Payload)).ConfigureAwait(false);
            }
        }
        finally
        {
            if (room.RemoveFriend(peerId))
            {
                _log($"room {room.Code}: peer {peerId} left");
                await room.SendToHostAsync(new PeerLeft(peerId)).ConfigureAwait(false);
            }
        }
    }

    private sealed class Room
    {
        private readonly object _gate = new();
        private byte _nextPeerId;

        public Room(string code, IConnection host)
        {
            Code = code;
            Host = host;
        }

        public string Code { get; }
        public IConnection Host { get; }
        public Dictionary<byte, IConnection> Friends { get; } = new();

        public ValueTask SendToHostAsync(RelayFrame frame) => Host.SendAsync(RelaySerializer.Serialize(frame));

        public bool TryAddFriend(IConnection friend, out byte peerId)
        {
            lock (_gate)
            {
                peerId = 0;
                if (Friends.Count >= MaxFriendsPerRoom)
                    return false;
                // Peer ids 1..254 are handed out round-robin so a reconnecting friend never reuses a stale id soon.
                do
                {
                    _nextPeerId = (byte)(_nextPeerId % 254 + 1);
                } while (Friends.ContainsKey(_nextPeerId));
                peerId = _nextPeerId;
                Friends.Add(peerId, friend);
                return true;
            }
        }

        public bool RemoveFriend(byte peerId)
        {
            lock (_gate)
                return Friends.Remove(peerId);
        }

        public bool TryGetFriend(byte peerId, out IConnection friend)
        {
            lock (_gate)
                return Friends.TryGetValue(peerId, out friend!);
        }

        public List<IConnection> SnapshotFriends()
        {
            lock (_gate)
                return new List<IConnection>(Friends.Values);
        }
    }
}
