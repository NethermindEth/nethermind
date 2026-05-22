// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Kademlia;

/// <summary>
/// Fixed-width 256-bit identifier used by the Kademlia routing table and XOR-distance operations.
/// </summary>
public readonly struct KademliaHash : IComparable<KademliaHash>, IEquatable<KademliaHash>
{
    private readonly ValueHash256 _value;

    /// <summary>
    /// Number of bytes in a Kademlia hash.
    /// </summary>
    public const int Length = 32;

    /// <summary>
    /// The all-zero hash value.
    /// </summary>
    public static KademliaHash Zero { get; } = new(new ValueHash256());

    /// <summary>
    /// Creates a hash from a hexadecimal string.
    /// </summary>
    /// <param name="hex">A 32-byte hexadecimal string, with or without the <c>0x</c> prefix.</param>
    public KademliaHash(string hex)
        : this(new ValueHash256(hex))
    {
    }

    private KademliaHash(ValueHash256 value) => _value = value;

    /// <summary>
    /// Gets the hash bytes.
    /// </summary>
    public ReadOnlySpan<byte> Bytes => _value.BytesAsSpan;

    /// <summary>
    /// Creates a hash from exactly 32 bytes.
    /// </summary>
    /// <param name="bytes">The bytes to copy into the hash.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="bytes"/> is not 32 bytes long.</exception>
    public static KademliaHash FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Length)
        {
            throw new ArgumentException($"Kademlia hash must be {Length} bytes.", nameof(bytes));
        }

        return new KademliaHash(new ValueHash256(bytes));
    }

    /// <summary>
    /// Copies the hash into a new byte array.
    /// </summary>
    public byte[] ToArray() => _value.Bytes.ToArray();

    /// <inheritdoc/>
    public int CompareTo(KademliaHash other) => _value.CompareTo(other._value);

    /// <inheritdoc/>
    public bool Equals(KademliaHash other) => _value == other._value;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is KademliaHash other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _value.GetHashCode();

    /// <inheritdoc/>
    public override string ToString() => _value.ToString();

    /// <summary>
    /// Returns whether two hashes contain the same bytes.
    /// </summary>
    public static bool operator ==(KademliaHash left, KademliaHash right) => left.Equals(right);

    /// <summary>
    /// Returns whether two hashes contain different bytes.
    /// </summary>
    public static bool operator !=(KademliaHash left, KademliaHash right) => !left.Equals(right);
}
