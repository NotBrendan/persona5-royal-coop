using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using P5RCoop.Protocol;
using P5RCoop.Protocol.Relay;
using P5RCoop.Protocol.Transport;

namespace P5RCoop.Friend;

/// <summary>Joining failed: unknown code, version mismatch, host refused, ...</summary>
public sealed class JoinFailedException : Exception
{
    public JoinFailedException(string reason, Exception? inner = null) : base(reason, inner) { }
}

/// <summary>
/// A friend's connection to a host through the relay. Tracks the seat table and whose turn it is, and
/// only sends key presses while the friend's own slot is acting; other keys are dropped locally with a
/// visible warning (the host would drop them anyway).
/// </summary>
public sealed class FriendClient : IDisposable
{
    private readonly object _gate = new();
    private readonly RelayClient _relay;
    private readonly IFriendUi _ui;
    private readonly CancellationTokenSource _cts = new();
    private IReadOnlyList<Seat> _seats = Array.Empty<Seat>();
    private byte? _currentSlot;
    private uint _sequence;
    private uint _pingNonce;
    private TaskCompletionSource<SeatClaimResult>? _pendingClaim;

    private FriendClient(RelayClient relay, IFriendUi ui, Welcome welcome)
    {
        _relay = relay;
        _ui = ui;
        PlayerId = welcome.PlayerId;
        HostName = welcome.HostName;
    }

    public byte PlayerId { get; }
    public string HostName { get; }

    /// <summary>Completes when the connection ends; <see cref="DisconnectReason"/> says why.</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    public string? DisconnectReason { get; private set; }

    /// <summary>Last measured round trip to the host through the relay.</summary>
    public TimeSpan? LastRoundTrip { get; private set; }

    /// <summary>Keys sent to the host / dropped locally because it was not our turn.</summary>
    public int SentKeys { get; private set; }
    public int DroppedKeys { get; private set; }

    public IReadOnlyList<Seat> Seats
    {
        get { lock (_gate) return _seats; }
    }

    public byte? CurrentSlot
    {
        get { lock (_gate) return _currentSlot; }
    }

    /// <summary>The slot this friend owns, according to the host's last seat table.</summary>
    public byte? MySlot
    {
        get
        {
            lock (_gate)
                return _seats.FirstOrDefault(s => s.OwnerId == PlayerId)?.Slot;
        }
    }

    public bool IsMyTurn
    {
        get
        {
            lock (_gate)
                return _currentSlot is byte slot && _seats.Any(s => s.Slot == slot && s.OwnerId == PlayerId);
        }
    }

    /// <summary>
    /// Joins room <paramref name="code"/> over <paramref name="connection"/>, says Hello and waits for the
    /// host's Welcome and seat table. Throws <see cref="JoinFailedException"/> with a readable reason.
    /// </summary>
    public static async Task<FriendClient> JoinAsync(IConnection connection, string code, string name, IFriendUi ui,
        TimeSpan? pingInterval = null, CancellationToken ct = default)
    {
        RelayClient relay;
        try
        {
            relay = await RelayClient.JoinRoomAsync(connection, code, ct).ConfigureAwait(false);
        }
        catch (RelayRejectedException e)
        {
            throw new JoinFailedException($"Relay refused: {e.Message}", e);
        }

        try
        {
            await relay.SendMessageAsync(new Hello(ProtocolInfo.Version, name), ct: ct).ConfigureAwait(false);

            Welcome? welcome = null;
            SeatAssignments? seats = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (seats is null)
            {
                var message = await ReceiveMessageAsync(relay, timeout.Token).ConfigureAwait(false);
                switch (message)
                {
                    case null:
                        throw new JoinFailedException("Disconnected before the host answered");
                    case Welcome { Accepted: false } rejected:
                        throw new JoinFailedException($"Host refused: {rejected.Reason}");
                    case Welcome accepted:
                        welcome = accepted;
                        break;
                    case SeatAssignments s when welcome is not null:
                        seats = s;
                        break;
                }
            }

            var client = new FriendClient(relay, ui, welcome!);
            client.ApplySeats(seats.Seats, announce: false);
            client.Completion = client.ReceiveLoopAsync(pingInterval ?? TimeSpan.FromSeconds(2));
            return client;
        }
        catch (ProtocolVersionException e)
        {
            relay.Dispose();
            throw new JoinFailedException(
                $"Version mismatch: host speaks protocol v{e.RemoteVersion}, this app speaks v{e.LocalVersion}", e);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            relay.Dispose();
            throw new JoinFailedException("Host did not answer within 10 s");
        }
        catch
        {
            relay.Dispose();
            throw;
        }
    }

    /// <summary>Asks the host for <paramref name="slot"/>; completes with the host's answer.</summary>
    public async Task<SeatClaimResult> ClaimSeatAsync(byte slot, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<SeatClaimResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
            _pendingClaim = tcs;
        await _relay.SendMessageAsync(new SeatClaim(slot), ct: ct).ConfigureAwait(false);
        using (ct.Register(() => tcs.TrySetCanceled(ct)))
        using (_cts.Token.Register(() => tcs.TrySetException(new JoinFailedException(DisconnectReason ?? "Disconnected"))))
            return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>Finds a seat by slot number or label (case-insensitive), e.g. "2" or "ann".</summary>
    public Seat? FindSeat(string text)
    {
        var seats = Seats;
        if (byte.TryParse(text, out var slot))
            return seats.FirstOrDefault(s => s.Slot == slot);
        return seats.FirstOrDefault(s => string.Equals(s.Label, text.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Sends <paramref name="keyCode"/> if it is our turn. Otherwise drops it and warns. Returns whether it was sent.
    /// </summary>
    public async Task<bool> PressKeyAsync(int keyCode, CancellationToken ct = default)
    {
        uint sequence;
        lock (_gate)
        {
            if (!IsMyTurn || !_relay.Connection.IsConnected)
            {
                DroppedKeys++;
                var why = !_relay.Connection.IsConnected ? "not connected"
                    : MySlot is null ? "you have no seat"
                    : _currentSlot is null ? "no battle turn right now"
                    : $"it is {DescribeSlot(_currentSlot.Value)}'s turn";
                _ui.Warn($"Dropped {KeyCodes.NameOf(keyCode)}: {why}");
                return false;
            }
            sequence = ++_sequence;
            SentKeys++;
        }
        await _relay.SendMessageAsync(new KeyPress(keyCode, sequence), ct: ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Feeds keys from <paramref name="source"/> until the connection ends or <paramref name="ct"/> fires.</summary>
    public async Task RunKeysAsync(IKeySource source, CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        try
        {
            await foreach (var key in source.ReadKeysAsync(linked.Token).ConfigureAwait(false))
                await PressKeyAsync(key, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task LeaveAsync(string reason = "bye")
    {
        await _relay.SendMessageAsync(new Leave(reason)).ConfigureAwait(false);
        Dispose();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _relay.Dispose();
    }

    private static async Task<Message?> ReceiveMessageAsync(RelayClient relay, CancellationToken ct)
    {
        while (true)
        {
            var frame = await relay.ReceiveAsync(ct).ConfigureAwait(false);
            switch (frame)
            {
                case null:
                    return null;
                case RelayData data:
                    return MessageSerializer.Deserialize(data.Payload);
                case RoomClosed closed:
                    throw new JoinFailedException($"Room closed: {closed.Reason}");
            }
        }
    }

    private async Task ReceiveLoopAsync(TimeSpan pingInterval)
    {
        var pinger = pingInterval > TimeSpan.Zero ? PingLoopAsync(pingInterval) : Task.CompletedTask;
        try
        {
            while (true)
            {
                var message = await ReceiveMessageAsync(_relay, _cts.Token).ConfigureAwait(false);
                if (message is null)
                {
                    DisconnectReason ??= "Connection to relay lost";
                    break;
                }
                Handle(message);
            }
        }
        catch (JoinFailedException e)
        {
            DisconnectReason = e.Message;
        }
        catch (ProtocolException e)
        {
            DisconnectReason = $"Bad data from host: {e.Message}";
        }
        catch (OperationCanceledException)
        {
            DisconnectReason ??= "Left";
        }
        finally
        {
            _ui.Warn($"Disconnected: {DisconnectReason}");
            _cts.Cancel();
            _relay.Dispose();
            await pinger.ConfigureAwait(false);
        }
    }

    private async Task PingLoopAsync(TimeSpan interval)
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(interval, _cts.Token).ConfigureAwait(false);
                await _relay.SendMessageAsync(new Ping(++_pingNonce, Environment.TickCount64)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Handle(Message message)
    {
        switch (message)
        {
            case SeatAssignments seats:
                ApplySeats(seats.Seats, announce: true);
                break;
            case TurnChanged turn:
                bool mine;
                lock (_gate)
                {
                    _currentSlot = turn.Slot;
                    mine = IsMyTurn;
                }
                _ui.Info(turn.Slot is null ? "No battle turn: host has control"
                    : mine ? $">>> YOUR TURN ({DescribeSlot(turn.Slot.Value)}) <<<"
                    : $"Turn: {DescribeSlot(turn.Slot.Value)}");
                break;
            case SeatClaimResult result:
                TaskCompletionSource<SeatClaimResult>? pending;
                lock (_gate)
                {
                    pending = _pendingClaim;
                    _pendingClaim = null;
                }
                if (!result.Accepted)
                    _ui.Warn($"Seat {result.Slot} refused: {result.Reason}");
                pending?.TrySetResult(result);
                break;
            case Pong pong:
                LastRoundTrip = TimeSpan.FromMilliseconds(Environment.TickCount64 - pong.TimestampMs);
                break;
            case Ping ping:
                _ = _relay.SendMessageAsync(new Pong(ping.Nonce, ping.TimestampMs));
                break;
            case Leave leave:
                DisconnectReason = $"Host left: {leave.Reason}";
                _cts.Cancel();
                break;
        }
    }

    private void ApplySeats(IReadOnlyList<Seat> seats, bool announce)
    {
        lock (_gate)
            _seats = seats;
        if (!announce)
            return;
        _ui.Info("Seats: " + string.Join(", ", seats.Select(s =>
            $"[{s.Slot}] {s.Label}: {(s.IsFree ? "host" : s.OwnerId == PlayerId ? "YOU" : s.OwnerName)}")));
    }

    private string DescribeSlot(byte slot)
    {
        var seat = _seats.FirstOrDefault(s => s.Slot == slot);
        if (seat is null)
            return $"host (slot {slot})";
        return seat.IsFree ? $"{seat.Label} (host)" : $"{seat.Label} ({seat.OwnerName})";
    }
}
