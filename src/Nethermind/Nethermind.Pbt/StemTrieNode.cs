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
        : Blake3Hash.HashPairOrZero(LeftHash.Bytes, RightHash.Bytes);

    public byte[] Encode()
    {
        if (IsStem)
        {
            byte[] data = new byte[1 + Stem.Length + 32];
            data[0] = StemTag;
            Stem.Bytes.CopyTo(data.AsSpan(1));
            LeafSubtreeRoot.Bytes.CopyTo(data.AsSpan(1 + Stem.Length));
            return data;
        }

        byte[] encoded = new byte[1 + 32 + 32];
        encoded[0] = InternalTag;
        LeftHash.Bytes.CopyTo(encoded.AsSpan(1));
        RightHash.Bytes.CopyTo(encoded.AsSpan(33));
        return encoded;
    }

    public static StemTrieNode Decode(ReadOnlySpan<byte> data) => data[0] switch
    {
        StemTag => StemNode(new Stem(data.Slice(1, Stem.Length)), new ValueHash256(data.Slice(1 + Stem.Length, 32))),
        InternalTag => new StemTrieNode { LeftHash = new ValueHash256(data.Slice(1, 32)), RightHash = new ValueHash256(data.Slice(33, 32)) },
        _ => throw new InvalidDataException($"Unknown stem trie node tag {data[0]}"),
    };
}
