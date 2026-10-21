// Compiled only when the mod defines P5RCOOP_NET (see docs/NETWORKING.md, "Wiring the mod").
#if P5RCOOP_NET
using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using P5RCoop.Protocol;
using P5RCoop.Protocol.Relay;
using P5RCoop.Protocol.Transport;

namespace p5rcoop.battle.Net;

/// <summary>
/// <see cref="IHostLink"/> over the relay server. Sends are queued and written by a background loop so
/// <see cref="HostSession"/> never blocks on the network (it calls us while holding its lock).
/// </summary>
public sealed class RelayHostLink : IHostLink, IDisposable
{
    private readonly RelayClient _relay;
    private readonly Channel<RelayFrame> _outgoing =
        Channel.CreateUnbounded<RelayFrame>(new UnboundedChannelOptions { SingleReader = true });

    private RelayHostLink(RelayClient relay)
    {
        _relay = relay;
    }

    /// <summary>Join code friends type in.</summary>
    public string Code => _relay.Code;

    /// <summary>Connects to the relay and opens a room. Throws <see cref="RelayRejectedException"/> if refused.</summary>
    public static async Task<RelayHostLink> OpenAsync(ITransport transport, string relayAddress, CancellationToken ct = default)
    {
        var connection = await transport.ConnectAsync(relayAddress, ct).ConfigureAwait(false);
        var relay = await RelayClient.CreateRoomAsync(connection, ct).ConfigureAwait(false);
        return new RelayHostLink(relay);
    }

    public void Send(byte peerId, Message message) =>
        _outgoing.Writer.TryWrite(new RelayData(peerId, MessageSerializer.Serialize(message)));

    public void Broadcast(Message message) =>
        _outgoing.Writer.TryWrite(new RelayData(RelayData.Broadcast, MessageSerializer.Serialize(message)));

    public void Kick(byte peerId) => _outgoing.Writer.TryWrite(new Kick(peerId));

    /// <summary>
    /// Pumps relay traffic into <paramref name="session"/> until the relay connection closes or
    /// <paramref name="ct"/> is cancelled. When it returns, the session has fallen back to host-only input.
    /// </summary>
    public async Task RunAsync(HostSession session, CancellationToken ct = default)
    {
        var sender = Task.Run(() => SendLoopAsync(ct), CancellationToken.None);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await _relay.ReceiveAsync(ct).ConfigureAwait(false);
                switch (frame)
                {
                    case null:
                        return;
                    case PeerJoined joined:
                        session.OnPeerJoined(joined.PeerId);
                        break;
                    case PeerLeft left:
                        session.OnPeerLeft(left.PeerId);
                        break;
                    case RelayData data:
                        session.OnFrame(data.PeerId, data.Payload);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (ProtocolException)
        {
            // Relay sent garbage; treat like a lost connection.
        }
        finally
        {
            session.OnLinkLost();
            _outgoing.Writer.TryComplete();
            _relay.Dispose();
            await sender.ConfigureAwait(false);
        }
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _outgoing.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                await _relay.SendAsync(frame, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _outgoing.Writer.TryComplete();
        _relay.Dispose();
    }
}
#endif
