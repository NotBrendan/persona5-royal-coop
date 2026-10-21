using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace P5RCoop.Protocol;

/// <summary>Little-endian writer for compact frames. Strings are UTF-8 with a 1-byte length (max 255 bytes).</summary>
public sealed class WireWriter
{
    private readonly MemoryStream _buffer = new(64);
    private readonly byte[] _scratch = new byte[8];

    public void U8(byte value) => _buffer.WriteByte(value);
    public void Bool(bool value) => U8(value ? (byte)1 : (byte)0);

    public void U16(ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_scratch, value);
        _buffer.Write(_scratch, 0, 2);
    }

    public void I32(int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(_scratch, value);
        _buffer.Write(_scratch, 0, 4);
    }

    public void U32(uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_scratch, value);
        _buffer.Write(_scratch, 0, 4);
    }

    public void I64(long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(_scratch, value);
        _buffer.Write(_scratch, 0, 8);
    }

    public void Str(string? value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        var length = bytes.Length;
        if (length > byte.MaxValue)
        {
            // Truncate on a character boundary so the result is still valid UTF-8.
            length = byte.MaxValue;
            while (length > 0 && (bytes[length] & 0xC0) == 0x80)
                length--;
        }
        U8((byte)length);
        _buffer.Write(bytes, 0, length);
    }

    public void Bytes(ReadOnlySpan<byte> value)
    {
        if (value.Length > ushort.MaxValue)
            throw new ArgumentException("Blob too large", nameof(value));
        U16((ushort)value.Length);
        _buffer.Write(value);
    }

    public byte[] ToArray() => _buffer.ToArray();
}

/// <summary>Reader matching <see cref="WireWriter"/>. Throws <see cref="ProtocolException"/> on truncated input.</summary>
public ref struct WireReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public WireReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
    }

    public int Remaining => _data.Length - _pos;

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || _pos + count > _data.Length)
            throw new ProtocolException("Frame truncated");
        var slice = _data.Slice(_pos, count);
        _pos += count;
        return slice;
    }

    public byte U8() => Take(1)[0];

    public bool Bool() => U8() switch
    {
        0 => false,
        1 => true,
        var other => throw new ProtocolException($"Bad bool value {other}"),
    };

    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    public string Str() => Encoding.UTF8.GetString(Take(U8()));
    public byte[] Bytes() => Take(U16()).ToArray();

    public void EnsureEnd()
    {
        if (Remaining != 0)
            throw new ProtocolException($"{Remaining} trailing bytes in frame");
    }
}
