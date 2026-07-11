// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Sparse;

/// <summary>
/// Stores either a raw RLP encoding (for inline/embedded nodes with RLP &lt; 32 bytes)
/// or a 32-byte keccak hash (for larger nodes). Used as the cached representation of
/// a node in the sparse trie after hashing.
/// </summary>
/// <remarks>
/// The hash case stores a <see cref="ValueHash256"/> inline (value type, 32 bytes on the
/// struct) instead of a heap <c>byte[32]</c>. The hash form is the overwhelmingly common
/// one â€” every dirty node â‰¥ 32 bytes produces one during the hash phase, thousands per
/// block â€” so eliminating that per-node allocation is the single biggest GC win on the
/// sparse hot path. Inline RLP (&lt; 32 bytes) still uses a <c>byte[]</c>, which is
/// already shared with the node's FullRlp buffer so it costs nothing extra here.
///
/// Discriminator: <see cref="_isHash"/> true â‡’ read <see cref="_hash"/>; false with
/// non-null <see cref="_data"/> â‡’ inline RLP; false with null <see cref="_data"/> â‡’ null/empty.
/// </remarks>
public readonly struct RlpNode : IEquatable<RlpNode>
{
    private readonly byte[]? _data;     // inline RLP bytes (only when !_isHash)
    private readonly ValueHash256 _hash; // 32-byte keccak (only when _isHash)
    private readonly bool _isHash;

    private RlpNode(byte[]? data)
    {
        _data = data;
        _isHash = false;
    }

    private RlpNode(in ValueHash256 hash)
    {
        _hash = hash;
        _isHash = true;
    }

    public static RlpNode FromRlp(byte[] rlp) => new(rlp);
    public static RlpNode FromHash(Hash256 hash) => new(hash.ValueHash256);

    /// <summary>Wraps a 32-byte hash span without any heap allocation.</summary>
    public static RlpNode FromHashSpan(ReadOnlySpan<byte> hashBytes) =>
        new(new ValueHash256(hashBytes));

    /// <summary>Computes keccak of the RLP bytes and stores the hash inline (no allocation).</summary>
    public static RlpNode FromRlpHashed(ReadOnlySpan<byte> rlp) =>
        new(ValueKeccak.Compute(rlp));

    /// <summary>
    /// Span over the underlying bytes. For the hash form this is the 32 hash bytes; for the
    /// inline form it is the raw RLP. Callers historically use this both as the child-ref
    /// payload and to read the hash, so both forms must expose their bytes.
    /// </summary>
    public ReadOnlySpan<byte> AsSpan() => _isHash ? _hash.Bytes : _data;

    public int Length => _isHash ? 32 : (_data?.Length ?? 0);
    public bool IsNull => !_isHash && _data is null;

    /// <summary>
    /// Internal accessor exposing the inline backing <c>byte[]</c> without a copy (null for
    /// the hash form). Callers that need a content-equal dictionary key for inline RLP can
    /// wrap this directly instead of <c>AsSpan().ToArray()</c>. Do not mutate.
    /// </summary>
    internal byte[]? UnderlyingBytes => _isHash ? null : _data;

    /// <summary>True if this was created from a hash (not from raw RLP).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsHash() => _isHash;

    /// <summary>
    /// Returns the hash. If this is already a 32-byte hash, returns it directly.
    /// If this is inline RLP (&lt; 32 bytes), computes keccak on the fly.
    /// </summary>
    public Hash256 AsHash()
    {
        if (_isHash) return _hash.ToCommitment();
        if (_data is null) return Keccak.EmptyTreeHash;
        return Keccak.Compute(_data);
    }

    /// <summary>
    /// Returns the hash as a value type, avoiding the <see cref="Hash256"/> class allocation
    /// that <see cref="AsHash"/> incurs. Prefer this on hot paths that immediately re-hash or
    /// compare; only convert to <see cref="Hash256"/> at reader/persistence boundaries.
    /// </summary>
    public ValueHash256 AsValueHash()
    {
        if (_isHash) return _hash;
        if (_data is null) return Keccak.EmptyTreeHash.ValueHash256;
        return ValueKeccak.Compute(_data);
    }

    /// <summary>
    /// Writes this node as a child reference in a parent node's RLP encoding.
    /// If the node's RLP is >= 32 bytes, writes the keccak hash (33 bytes: 0xa0 prefix + hash).
    /// If &lt; 32 bytes, writes the raw inline RLP.
    /// If null, writes 0x80 (empty).
    /// </summary>
    public int WriteChildRef(Span<byte> buffer)
    {
        if (_isHash)
        {
            buffer[0] = 0xa0;
            _hash.Bytes.CopyTo(buffer[1..]);
            return 33;
        }
        if (_data is null)
        {
            buffer[0] = 0x80;
            return 1;
        }
        if (_data.Length >= 32)
        {
            buffer[0] = 0xa0;
            ValueHash256 hash = ValueKeccak.Compute(_data);
            hash.Bytes.CopyTo(buffer[1..]);
            return 33;
        }
        // Inline RLP (< 32 bytes)
        _data.AsSpan().CopyTo(buffer);
        return _data.Length;
    }

    /// <summary>RLP length contribution when this node is a child reference in a parent.</summary>
    public int ChildRefLength => _isHash ? 33 : _data is null ? 1 : _data.Length >= 32 ? 33 : _data.Length;

    public bool Equals(RlpNode other)
    {
        if (_isHash || other._isHash)
        {
            // Mixed forms can still be content-equal (a hash form vs an inline form that
            // hashes to it would not be, by design these never compare equal). Compare by
            // discriminator first, then by payload.
            return _isHash == other._isHash && _hash == other._hash;
        }
        return AsSpan().SequenceEqual(other.AsSpan());
    }

    public override bool Equals(object? obj) => obj is RlpNode other && Equals(other);

    public override int GetHashCode()
    {
        if (_isHash) return _hash.GetHashCode();
        if (_data is null) return 0;
        HashCode hc = new();
        hc.AddBytes(_data);
        return hc.ToHashCode();
    }
}
