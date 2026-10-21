using System.Collections.Generic;
using System.Threading;

namespace P5RCoop.Friend;

/// <summary>Where the friend's key presses come from. Yields <c>KeyCodes</c> values.</summary>
public interface IKeySource
{
    IAsyncEnumerable<int> ReadKeysAsync(CancellationToken cancellationToken);
}

/// <summary>What the friend sees.</summary>
public interface IFriendUi
{
    /// <summary>Informational line (turn changes, seat table, latency).</summary>
    void Info(string line);

    /// <summary>Something the user should notice (dropped key, rejected claim, disconnect).</summary>
    void Warn(string line);
}
