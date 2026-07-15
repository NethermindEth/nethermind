// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>
/// A mutable stem trie node: either an internal node holding its two child hashes, or a stem node
/// holding its stem and the root of its 256-leaf subtree (the stem node hash itself is derived, never stored).
/// </summary>
/// <remarks>
/// The payloads are fields, not properties: <see cref="ValueHash256.Bytes"/> over a
/// property-returned copy is a span into a dead stack temporary the JIT may reuse.
/// </remarks>
public sealed class StemTrieNode
{
    private const byte InternalTag = 0x00;
    private const byte StemTag = 0x01;

    public bool IsStem { get; private init; }
    public Stem Stem;
    public ValueHash256 LeafSubtreeRoot;
    public ValueHash256 LeftHash;
    public ValueHash256 RightHash;

    public static StemTrieNode Internal() => new();

    public static StemTrieNode StemNode(in Stem stem, in ValueHash256 leafSubtreeRoot) =>
        new() { IsStem = true, Stem = stem, LeafSubtreeRoot = leafSubtreeRoot };

    public ValueHash256 ComputeHash() => IsStem
        ? StemLeafBlob.ComputeStemNodeHash(Stem, LeafSubtreeRoot)
        : Blake3Hash.HashPairOrZero(LeftHash, RightHash);

    /// <summary>Largest encoded node size: internal node = tag + two 32-byte hashes.</summary>
    public const int MaxEncodedLength = 1 + 32 + 32;

    /// <summary>Encodes the node into <paramref name="destination"/> (≥ <see cref="MaxEncodedLength"/> bytes) and returns the length written.</summary>
    public int Encode(Span<byte> destination)
    {
        if (IsStem)
        {
            destination[0] = StemTag;
            Stem.Bytes.CopyTo(destination[1..]);
            LeafSubtreeRoot.Bytes.CopyTo(destination[(1 + Stem.Length)..]);
            return 1 + Stem.Length + 32;
        }

        destination[0] = InternalTag;
        LeftHash.Bytes.CopyTo(destination[1..]);
        RightHash.Bytes.CopyTo(destination[33..]);
        return MaxEncodedLength;
    }

    public static StemTrieNode Decode(ReadOnlySpan<byte> data) => data[0] switch
    {
        StemTag => StemNode(new Stem(data.Slice(1, Stem.Length)), new ValueHash256(data.Slice(1 + Stem.Length, 32))),
        InternalTag => new StemTrieNode { LeftHash = new ValueHash256(data.Slice(1, 32)), RightHash = new ValueHash256(data.Slice(33, 32)) },
        _ => throw new InvalidDataException($"Unknown stem trie node tag {data[0]}"),
    };
}
