using System;

namespace P5RCoop.Protocol;

/// <summary>A frame could not be decoded (truncated, unknown type, bad field).</summary>
public class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }
}

/// <summary>A frame was written by a peer speaking a different protocol version.</summary>
public sealed class ProtocolVersionException : ProtocolException
{
    public ProtocolVersionException(int remoteVersion, int localVersion)
        : base($"Protocol version mismatch: remote v{remoteVersion}, local v{localVersion}")
    {
        RemoteVersion = remoteVersion;
        LocalVersion = localVersion;
    }

    public int RemoteVersion { get; }
    public int LocalVersion { get; }
}
