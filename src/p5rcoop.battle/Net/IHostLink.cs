// Compiled only when the mod defines P5RCOOP_NET (see docs/NETWORKING.md, "Wiring the mod").
#if P5RCOOP_NET
using P5RCoop.Protocol;

namespace p5rcoop.battle.Net;

/// <summary>
/// How the host session reaches friends. <see cref="RelayHostLink"/> goes through the relay server; a
/// future Steam lobby link would implement the same three calls. All calls must be non-blocking
/// (they are made while <see cref="HostSession"/> holds its lock).
/// </summary>
public interface IHostLink
{
    void Send(byte peerId, Message message);

    void Broadcast(Message message);

    /// <summary>Disconnects the peer. The link reports it back via <see cref="HostSession.OnPeerLeft"/>.</summary>
    void Kick(byte peerId);
}
#endif
