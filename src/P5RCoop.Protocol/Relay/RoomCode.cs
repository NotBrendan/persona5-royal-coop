using System.Security.Cryptography;

namespace P5RCoop.Protocol.Relay;

/// <summary>Short join codes: 6 characters from an alphabet without look-alikes (no 0/O, 1/I/L).</summary>
public static class RoomCode
{
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public const int Length = 6;

    public static string Generate() => RandomNumberGenerator.GetString(Alphabet, Length);

    /// <summary>Upper-cases and trims user input; codes are case-insensitive.</summary>
    public static string Normalize(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant();
}
