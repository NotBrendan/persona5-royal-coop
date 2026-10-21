// Compiled only when the mod defines P5RCOOP_NET (see docs/NETWORKING.md, "Wiring the mod").
#if P5RCOOP_NET
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using P5RCoop.Protocol;

namespace p5rcoop.battle.Net;

/// <summary>
/// The authoritative co-op state on the host: who is connected, who owns which party slot, whose turn it
/// is, and whether the game sees the host's keys or a friend's keys.
///
/// Input rules (applied in <see cref="FilterInput"/> on the game thread):
/// - A friend-owned slot is acting: the host's keys are removed; that friend's keys are injected in
///   sequence order, duplicates dropped.
/// - The host's own slot, an unclaimed slot, or no battle: host keys pass; friend keys are dropped.
/// - The owning friend disconnects mid-turn: the slot falls back to the host at once.
///
/// Thread-safe: network callbacks, turn changes and input polls may arrive on different threads.
/// </summary>
public sealed class HostSession : IDisposable
{
    private readonly object _gate = new();
    private readonly HostSessionOptions _options;
    private readonly ITurnSource _turns;
    private readonly IInputSink _input;
    private readonly IHostLink _link;
    private readonly Dictionary<byte, Peer> _peers = new();
    private readonly Dictionary<byte, byte> _owners = new(); // slot -> peer id
    private readonly Dictionary<byte, SeatDefinition> _seats;
    private readonly List<PendingKey> _pending = new();       // sent to the sink, not yet seen in a poll
    private readonly List<int> _released = new();
    private byte? _currentSlot;
    private int _turnEpoch;
    private bool _disposed;

    public HostSession(HostSessionOptions options, ITurnSource turns, IInputSink input, IHostLink link)
    {
        _options = options;
        _turns = turns;
        _input = input;
        _link = link;
        _seats = options.Seats.ToDictionary(s => s.Slot);
        _currentSlot = turns.CurrentSlot;
        _turns.TurnChanged += OnTurnChanged;
        _input.Intercept += FilterInput;
    }

    /// <summary>Keys injected into the game on behalf of friends.</summary>
    public long InjectedKeys { get; private set; }

    /// <summary>Friend keys dropped because it was not the sender's turn.</summary>
    public long DroppedOffTurnKeys { get; private set; }

    /// <summary>Friend keys dropped as duplicates (same sequence number seen before).</summary>
    public long DuplicateKeys { get; private set; }

    /// <summary>Host keys removed because a friend was acting.</summary>
    public long BlockedHostKeys { get; private set; }

    public byte? CurrentSlot
    {
        get { lock (_gate) return _currentSlot; }
    }

    /// <summary>Peer id of the friend whose turn it is, or null when the host has control.</summary>
    public byte? ActingPeer
    {
        get { lock (_gate) return ActingPeerLocked(); }
    }

    public IReadOnlyList<Seat> Seats
    {
        get { lock (_gate) return BuildSeats(); }
    }

    /// <summary>Welcomed friends by peer id.</summary>
    public IReadOnlyDictionary<byte, string> Players
    {
        get
        {
            lock (_gate)
                return _peers.Values.Where(p => p.Welcomed).ToDictionary(p => p.Id, p => p.Name);
        }
    }

    // ---- network side -------------------------------------------------------------------------

    public void OnPeerJoined(byte peerId)
    {
        lock (_gate)
        {
            _peers[peerId] = new Peer(peerId, _options.MaxReorderPending);
            Log($"peer {peerId} connected, waiting for Hello");
        }
    }

    public void OnPeerLeft(byte peerId)
    {
        lock (_gate)
            RemovePeerLocked(peerId, "disconnected");
    }

    /// <summary>The relay connection itself dropped: every friend is gone, the host keeps playing.</summary>
    public void OnLinkLost()
    {
        lock (_gate)
        {
            foreach (var id in _peers.Keys.ToList())
                RemovePeerLocked(id, "relay connection lost", broadcast: false);
        }
    }

    /// <summary>A raw frame from a friend (as forwarded by the relay).</summary>
    public void OnFrame(byte peerId, ReadOnlySpan<byte> frame)
    {
        Message message;
        try
        {
            message = MessageSerializer.Deserialize(frame);
        }
        catch (ProtocolVersionException e)
        {
            lock (_gate)
            {
                Log($"peer {peerId} speaks protocol v{e.RemoteVersion}, need v{e.LocalVersion}: rejected");
                _link.Send(peerId, new Welcome(false, VersionReason(e.RemoteVersion), 0, _options.HostName));
                _link.Kick(peerId);
                RemovePeerLocked(peerId, "rejected (version)");
            }
            return;
        }
        catch (ProtocolException e)
        {
            lock (_gate)
            {
                Log($"peer {peerId} sent a bad frame ({e.Message}): kicked");
                _link.Kick(peerId);
                RemovePeerLocked(peerId, "kicked (bad frame)");
            }
            return;
        }
        OnMessage(peerId, message);
    }

    public void OnMessage(byte peerId, Message message)
    {
        lock (_gate)
        {
            if (_disposed || !_peers.TryGetValue(peerId, out var peer))
                return;

            if (message is Hello hello)
            {
                HandleHello(peer, hello);
                return;
            }
            if (!peer.Welcomed)
                return;

            switch (message)
            {
                case SeatClaim claim:
                    HandleSeatClaim(peer, claim.Slot);
                    break;
                case KeyPress key:
                    HandleKey(peer, key);
                    break;
                case Ping ping:
                    _link.Send(peer.Id, new Pong(ping.Nonce, ping.TimestampMs));
                    break;
                case Leave leave:
                    _link.Kick(peer.Id);
                    RemovePeerLocked(peer.Id, $"left ({leave.Reason})");
                    break;
            }
        }
    }

    private void HandleHello(Peer peer, Hello hello)
    {
        if (peer.Welcomed)
            return;
        if (hello.ProtocolVersion != ProtocolInfo.Version)
        {
            Log($"peer {peer.Id} Hello with protocol v{hello.ProtocolVersion}: rejected");
            _link.Send(peer.Id, new Welcome(false, VersionReason(hello.ProtocolVersion), 0, _options.HostName));
            _link.Kick(peer.Id);
            RemovePeerLocked(peer.Id, "rejected (version)");
            return;
        }

        peer.Name = SanitizeName(hello.Name, peer.Id);
        peer.Welcomed = true;
        Log($"peer {peer.Id} is {peer.Name}");
        _link.Send(peer.Id, new Welcome(true, string.Empty, peer.Id, _options.HostName));
        _link.Send(peer.Id, new SeatAssignments(BuildSeats()));
        _link.Send(peer.Id, new TurnChanged(_currentSlot));
    }

    private void HandleSeatClaim(Peer peer, byte slot)
    {
        if (!_seats.TryGetValue(slot, out var seat))
        {
            _link.Send(peer.Id, new SeatClaimResult(slot, false, "No such seat (the host plays that slot)"));
            return;
        }
        if (_owners.TryGetValue(slot, out var owner))
        {
            var accepted = owner == peer.Id;
            var reason = accepted ? string.Empty : $"{seat.Label} is taken by {_peers[owner].Name}";
            _link.Send(peer.Id, new SeatClaimResult(slot, accepted, reason));
            return;
        }

        // One seat per friend: claiming a new one releases the old one.
        if (peer.Slot is byte old)
            _owners.Remove(old);
        _owners[slot] = peer.Id;
        peer.Slot = slot;
        Log($"{peer.Name} now controls {seat.Label} (slot {slot})");
        // Table first, so the claimer already sees itself seated when the result arrives.
        _link.Broadcast(new SeatAssignments(BuildSeats()));
        _link.Send(peer.Id, new SeatClaimResult(slot, true, string.Empty));
    }

    private void HandleKey(Peer peer, KeyPress key)
    {
        _released.Clear();
        if (peer.Keys.Push(key.Sequence, key.Key, _released) == SequenceResult.Duplicate)
        {
            DuplicateKeys++;
            return;
        }

        foreach (var code in _released)
        {
            if (ActingPeerLocked() != peer.Id || !KeyCodes.IsKnown(code))
            {
                DroppedOffTurnKeys++;
                continue;
            }
            _pending.Add(new PendingKey(code, _turnEpoch));
            InjectedKeys++;
            _input.SendKey(code);
        }
    }

    // ---- game side ----------------------------------------------------------------------------

    private void OnTurnChanged(byte? slot)
    {
        lock (_gate)
        {
            if (_disposed || slot == _currentSlot)
                return;
            _currentSlot = slot;
            _turnEpoch++;
            var acting = ActingPeerLocked();
            Log(slot is null
                ? "turn: none (host input)"
                : acting is byte p
                    ? $"turn: slot {slot} -> {_peers[p].Name}"
                    : $"turn: slot {slot} -> host");
            _link.Broadcast(new TurnChanged(slot));
        }
    }

    /// <summary>
    /// Rewrites the keys the game is about to process. Hooked to <see cref="IInputSink.Intercept"/>.
    /// <paramref name="keys"/> is the host's rising-edge keys followed by keys we queued with SendKey.
    /// </summary>
    public void FilterInput(List<int> keys)
    {
        lock (_gate)
        {
            // Our injected keys are the tail of the list, in the order we sent them. Find how many
            // made it into this poll (normally all of them).
            var delivered = 0;
            for (var n = Math.Min(keys.Count, _pending.Count); n > 0; n--)
            {
                if (TailMatches(keys, n))
                {
                    delivered = n;
                    break;
                }
            }
            keys.RemoveRange(keys.Count - delivered, delivered);

            if (ActingPeerLocked() is not null)
            {
                BlockedHostKeys += keys.Count;
                keys.Clear();
                // Only keys sent during this turn; keys from a turn that already ended are discarded.
                for (var i = 0; i < delivered; i++)
                {
                    if (_pending[i].Epoch == _turnEpoch)
                        keys.Add(_pending[i].Key);
                }
            }
            _pending.RemoveRange(0, delivered);
        }
    }

    private bool TailMatches(List<int> keys, int n)
    {
        var offset = keys.Count - n;
        for (var i = 0; i < n; i++)
        {
            if (keys[offset + i] != _pending[i].Key)
                return false;
        }
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _turns.TurnChanged -= OnTurnChanged;
            _input.Intercept -= FilterInput;
            _link.Broadcast(new Leave("Host closed the session"));
        }
    }

    // ---- helpers ------------------------------------------------------------------------------

    private byte? ActingPeerLocked() =>
        _currentSlot is byte slot && _owners.TryGetValue(slot, out var peer) ? peer : null;

    private void RemovePeerLocked(byte peerId, string why, bool broadcast = true)
    {
        if (!_peers.Remove(peerId, out var peer))
            return;
        var wasActing = ActingPeerLocked() == peerId;
        if (peer.Slot is byte slot)
            _owners.Remove(slot);
        Log($"{(peer.Welcomed ? peer.Name : $"peer {peerId}")} {why}");
        if (wasActing)
            Log($"slot {_currentSlot} falls back to host input");
        if (broadcast && peer.Slot is not null)
            _link.Broadcast(new SeatAssignments(BuildSeats()));
    }

    private List<Seat> BuildSeats() =>
        _options.Seats
            .Select(s => _owners.TryGetValue(s.Slot, out var owner)
                ? new Seat(s.Slot, s.Label, owner, _peers[owner].Name)
                : new Seat(s.Slot, s.Label, Seat.NoOwner, string.Empty))
            .ToList();

    private static string VersionReason(int remote) =>
        $"Protocol mismatch: host speaks v{ProtocolInfo.Version}, you sent v{remote}. Update P5RCoop.";

    private static string SanitizeName(string name, byte peerId)
    {
        var sb = new StringBuilder();
        foreach (var c in name.Trim())
        {
            if (!char.IsControl(c))
                sb.Append(c);
            if (sb.Length == ProtocolInfo.MaxNameLength)
                break;
        }
        return sb.Length == 0 ? $"Friend {peerId}" : sb.ToString();
    }

    private void Log(string line) => _options.Log?.Invoke(line);

    private readonly record struct PendingKey(int Key, int Epoch);

    private sealed class Peer
    {
        public Peer(byte id, int maxReorderPending)
        {
            Id = id;
            Keys = new SequenceReorderBuffer<int>(maxReorderPending);
        }

        public byte Id { get; }
        public string Name { get; set; } = string.Empty;
        public bool Welcomed { get; set; }
        public byte? Slot { get; set; }
        public SequenceReorderBuffer<int> Keys { get; }
    }
}
#endif
