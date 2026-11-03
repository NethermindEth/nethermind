using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Paprika.Store;

/// <summary>
/// Represents an address in the database. It can be one of the following:
///
/// 1. an address page
/// 2. a data-frame page in the same page as the value resides in
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = sizeof(byte), Size = Size)]
public readonly struct DbAddress : IEquatable<DbAddress>
{
    /// <summary>
    /// The null address.
    /// </summary>
    public static readonly DbAddress Null = default;

    public const int Size = sizeof(uint);

    // private const uint JumpCounter = byte.MaxValue - (SamePage >> JumpCountShift);
    private const int NullValue = 0;

    private readonly uint _value;

    public uint Raw => _value;

    /// <summary>
    /// Gets the offset in the file.
    /// </summary>
    /// <remarks>
    /// Long here is required! Otherwise int overflow will turn it to negative value!
    /// </remarks>
    public long FileOffset => (long)_value * Store.Page.PageSize;

    /// <summary>
    /// Creates a database address that represents a jump to another database <see cref="Store.Page"/>.
    /// </summary>
    /// <param name="page">The page to go to.</param>
    /// <returns></returns>
    public static DbAddress Page(uint page) => new(page);

    /// <summary>
    /// Gets the next address.
    /// </summary>
    public DbAddress Next => new(_value + 1);

    public DbAddress(uint value) => _value = value;

    public bool IsNull => _value == NullValue;

    public static implicit operator uint(DbAddress address) => address._value;
    public static implicit operator int(DbAddress address) => (int)address._value;

    public override string ToString() => IsNull ? "null" : $"Page @{_value}";

    public bool Equals(DbAddress other) => _value == other._value;

    public override bool Equals(object? obj) => obj is DbAddress other && Equals(other);

    public override int GetHashCode() => (int)_value;

    public static DbAddress Read(ReadOnlySpan<byte> data) => new(BinaryPrimitives.ReadUInt32LittleEndian(data));

    public void Write(Span<byte> destination) => BinaryPrimitives.WriteUInt32LittleEndian(destination, _value);
}
