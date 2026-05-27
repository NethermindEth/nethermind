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
public readonly struct RlpNode : IEquatable<RlpNode>
{
    private readonly byte[] _data;
    private readonly bool _isHash;

    private RlpNode(byte[] data, bool isHash)
    {
        _data = data;
        _isHash = isHash;
    }

    public static RlpNode FromRlp(byte[] rlp) => new(rlp, isHash: false);
    public static RlpNode FromHash(Hash256 hash) => new(hash.Bytes.ToArray(), isHash: true);

    /// <summary>Avoids the intermediate Hash256 class allocation when wrapping a 32-byte span.</summary>
    public static RlpNode FromHashSpan(ReadOnlySpan<byte> hashBytes)
    {
        byte[] data = new byte[32];
        hashBytes.CopyTo(data);
        return new(data, isHash: true);
    }

    public ReadOnlySpan<byte> AsSpan() => _data;
    public int Length => _data?.Length ?? 0;
    public bool IsNull => _data is null;

    /// <summary>True if this was explicitly created from a hash (not from raw RLP).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsHash() => _isHash;

    /// <summary>
    /// Returns the hash. If this is already a 32-byte hash, returns it directly.
    /// If this is inline RLP (&lt; 32 bytes), computes keccak on the fly.
    /// </summary>
    public Hash256 AsHash()
    {
        if (_data is null) return Keccak.EmptyTreeHash;
        if (_isHash) return new Hash256(_data);
        return Keccak.Compute(_data);
    }

    /// <summary>
    /// Returns the RLP bytes to embed as a child reference in a parent node's encoding.
    /// For hash nodes: RLP-encoded hash (33 bytes: 0xa0 prefix + 32 bytes).
    /// For inline nodes: raw RLP bytes (as-is).
    /// </summary>
    /// <summary>
    /// Writes this node as a child reference in a parent node's RLP encoding.
    /// If the node's RLP is >= 32 bytes, writes the keccak hash (33 bytes: 0xa0 prefix + hash).
    /// If &lt; 32 bytes, writes the raw inline RLP.
    /// If null, writes 0x80 (empty).
    /// </summary>
    public int WriteChildRef(Span<byte> buffer)
    {
        if (_data is null)
        {
            buffer[0] = 0x80;
            return 1;
        }
        if (_isHash)
        {
            buffer[0] = 0xa0;
            _data.AsSpan().CopyTo(buffer[1..]);
            return 33;
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
    public int ChildRefLength => _data is null ? 1 : (_isHash || _data.Length >= 32) ? 33 : Length;

    public bool Equals(RlpNode other) => AsSpan().SequenceEqual(other.AsSpan());
    public override bool Equals(object? obj) => obj is RlpNode other && Equals(other);
    public override int GetHashCode()
    {
        if (_data is null) return 0;
        HashCode hc = new();
        hc.AddBytes(_data);
        return hc.ToHashCode();
    }
}
