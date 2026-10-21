// Compiled only when the mod defines P5RCOOP_NET (see docs/NETWORKING.md, "Wiring the mod").
#if P5RCOOP_NET
using System;
using System.Threading;
using System.Threading.Tasks;
using P5RCoop.Protocol.Transport;

namespace p5rcoop.battle.Net;

/// <summary>
/// One-call setup for Mod.cs: connect to the relay, open a room, run a <see cref="HostSession"/>.
/// <code>
/// var host = await CoopHost.StartAsync(new TcpTransport(), "relay.example:7777", options, turns, sink);
/// log($"Join code: {host.Code}");
/// </code>
/// </summary>
public sealed class CoopHost : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly RelayHostLink _link;

    private CoopHost(RelayHostLink link, HostSession session, CancellationTokenSource cts)
    {
        _link = link;
        Session = session;
        _cts = cts;
        Completion = link.RunAsync(session, cts.Token);
    }

    public string Code => _link.Code;
    public HostSession Session { get; }

    /// <summary>Completes when the relay connection ends.</summary>
    public Task Completion { get; }

    public static async Task<CoopHost> StartAsync(ITransport transport, string relayAddress, HostSessionOptions options,
        ITurnSource turns, IInputSink input, CancellationToken ct = default)
    {
        var link = await RelayHostLink.OpenAsync(transport, relayAddress, ct).ConfigureAwait(false);
        var session = new HostSession(options, turns, input, link);
        return new CoopHost(link, session, new CancellationTokenSource());
    }

    public void Dispose()
    {
        if (_cts.IsCancellationRequested)
            return;
        Session.Dispose();
        _cts.Cancel();
        _link.Dispose();
    }
}
#endif
