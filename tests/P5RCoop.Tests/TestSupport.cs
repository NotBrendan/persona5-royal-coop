using System.Collections.Concurrent;
using p5rcoop.battle.Net;
using P5RCoop.Friend;
using P5RCoop.Protocol;

namespace P5RCoop.Tests;

internal static class Wait
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Polls <paramref name="condition"/> until true; fails the test after <see cref="Timeout"/>.</summary>
    public static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(5);
        }
    }

    public static async Task<T> WithTimeout<T>(this Task<T> task)
    {
        var done = await Task.WhenAny(task, Task.Delay(Timeout));
        if (done != task)
            throw new TimeoutException("Task did not complete in time");
        return await task;
    }
}

/// <summary>Records what a friend would see.</summary>
internal sealed class RecordingUi : IFriendUi
{
    public ConcurrentQueue<string> Infos { get; } = new();
    public ConcurrentQueue<string> Warnings { get; } = new();

    public void Info(string line) => Infos.Enqueue(line);
    public void Warn(string line) => Warnings.Enqueue(line);
}

/// <summary>IHostLink that records instead of sending.</summary>
internal sealed class RecordingLink : IHostLink
{
    public List<(byte Peer, Message Message)> Sent { get; } = new();
    public List<Message> Broadcasts { get; } = new();
    public List<byte> Kicked { get; } = new();

    public void Send(byte peerId, Message message) => Sent.Add((peerId, message));
    public void Broadcast(Message message) => Broadcasts.Add(message);
    public void Kick(byte peerId) => Kicked.Add(peerId);

    public IEnumerable<T> SentTo<T>(byte peer) where T : Message =>
        Sent.Where(s => s.Peer == peer).Select(s => s.Message).OfType<T>();
}

/// <summary>Key source the test feeds by hand.</summary>
internal sealed class ScriptedKeySource : IKeySource
{
    private readonly System.Threading.Channels.Channel<int> _keys = System.Threading.Channels.Channel.CreateUnbounded<int>();

    public void Press(params int[] keys)
    {
        foreach (var key in keys)
            _keys.Writer.TryWrite(key);
    }

    public IAsyncEnumerable<int> ReadKeysAsync(CancellationToken cancellationToken) =>
        _keys.Reader.ReadAllAsync(cancellationToken);
}
