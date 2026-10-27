using System;
using System.Buffers.Binary;
using System.IO.Hashing;

namespace SauerbratenRevival.World.Runtime.Formats.Save;

/// <summary>
/// A 128-bit content identifier computed via <see cref="XxHash128"/> (ADR 0008).
/// Immutable, stack-allocated, zero-allocation struct representation.
/// Serialized as 16 Little-Endian bytes or a 32-character lowercase hex string.
/// </summary>
public readonly struct ObjectId : IEquatable<ObjectId>, IComparable<ObjectId>
{
    public readonly ulong High;
    public readonly ulong Low;

    public static readonly ObjectId Empty = new(0, 0);

    public ObjectId(ulong high, ulong low)
    {
        High = high;
        Low = low;
    }

    public ObjectId(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16) throw new ArgumentException("ObjectId requires at least 16 bytes", nameof(bytes));
        High = BinaryPrimitives.ReadUInt64LittleEndian(bytes[..8]);
        Low = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(8, 8));
    }

    public static ObjectId Read(ReadOnlySpan<byte> bytes) => new(bytes);

    /// <summary>Compute the 128-bit ObjectId of a byte buffer using SIMD-accelerated XxHash128.</summary>
    public static ObjectId Compute(ReadOnlySpan<byte> data)
    {
        Span<byte> destination = stackalloc byte[16];
        XxHash128.Hash(data, destination);
        // destination contains the 16-byte XxHash128 hash in big-endian order from Hash
        // Read consistently
        ulong high = BinaryPrimitives.ReadUInt64BigEndian(destination[..8]);
        ulong low = BinaryPrimitives.ReadUInt64BigEndian(destination.Slice(8, 8));
        return new ObjectId(high, low);
    }

    public void Write(Span<byte> destination)
    {
        if (destination.Length < 16) throw new ArgumentException("Destination requires at least 16 bytes", nameof(destination));
        BinaryPrimitives.WriteUInt64LittleEndian(destination[..8], High);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(8, 8), Low);
    }

    public byte[] ToByteArray()
    {
        byte[] bytes = new byte[16];
        Write(bytes);
        return bytes;
    }

    public static ObjectId Parse(string hex)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length != 32)
            throw new FormatException($"Invalid ObjectId hex string length (expected 32, got {hex?.Length ?? 0})");

        if (!ulong.TryParse(hex.AsSpan(0, 16), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out ulong high) ||
            !ulong.TryParse(hex.AsSpan(16, 16), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out ulong low))
        {
            throw new FormatException($"Invalid ObjectId hex string '{hex}'");
        }

        return new ObjectId(high, low);
    }

    public static bool TryParse(string? hex, out ObjectId id)
    {
        id = Empty;
        if (string.IsNullOrEmpty(hex) || hex.Length != 32) return false;

        if (!ulong.TryParse(hex.AsSpan(0, 16), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out ulong high) ||
            !ulong.TryParse(hex.AsSpan(16, 16), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out ulong low))
        {
            return false;
        }

        id = new ObjectId(high, low);
        return true;
    }

    public bool IsEmpty => High == 0 && Low == 0;

    public bool Equals(ObjectId other) => High == other.High && Low == other.Low;

    public override bool Equals(object? obj) => obj is ObjectId other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(High, Low);

    public int CompareTo(ObjectId other)
    {
        int c = High.CompareTo(other.High);
        return c != 0 ? c : Low.CompareTo(other.Low);
    }

    public static bool operator ==(ObjectId left, ObjectId right) => left.Equals(right);
    public static bool operator !=(ObjectId left, ObjectId right) => !left.Equals(right);

    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(bytes[..8], High);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.Slice(8, 8), Low);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
