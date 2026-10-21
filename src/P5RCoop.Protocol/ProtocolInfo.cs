namespace P5RCoop.Protocol;

/// <summary>Version constants for the game protocol (host &lt;-&gt; friend messages).</summary>
public static class ProtocolInfo
{
    /// <summary>
    /// Game protocol version. Bump on any wire-incompatible change to <see cref="MessageSerializer"/>.
    /// Peers with a different version are rejected by the host.
    /// </summary>
    public const ushort Version = 1;

    /// <summary>Longest player name the host keeps (in UTF-16 chars); longer names are truncated.</summary>
    public const int MaxNameLength = 24;

    /// <summary>Largest frame any component accepts (bytes).</summary>
    public const int MaxFrameSize = 4096;
}
