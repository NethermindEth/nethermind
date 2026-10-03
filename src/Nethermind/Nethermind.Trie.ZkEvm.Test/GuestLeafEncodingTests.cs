// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Buffers;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.ZkEvm.Test;

/// <summary>Tests for the guest's leaf encoder, which reuses the key item of a leaf's previous RLP.</summary>
/// <remarks>
/// Encodes without hashing: in a ZK_EVM build the Keccak permutation is a zkVM precompile that throws outside the guest.
/// The expected RLP comes from a leaf built from scratch, which has no previous RLP to reuse.
/// </remarks>
[Parallelizable(ParallelScope.All)]
public class GuestLeafEncodingTests
{
    [Test]
    public void Value_change_keeps_the_key([Values(0, 1, 2, 31, 64)] int keyLength, [Values(1, 33, 56, 120)] int valueLength)
    {
        byte[] key = Nibbles(keyLength);
        TrieNode changed = DecodedLeaf(key, Bytes(40, 0x11)).Unseal();
        changed.Value = new CappedArray<byte>(Bytes(valueLength, 0x22));

        Assert.That(Encode(changed), Is.EqualTo(Encode(TrieNodeFactory.CreateLeaf(key, new CappedArray<byte>(Bytes(valueLength, 0x22))))));
    }

    [Test]
    public void Key_change_drops_the_previous_key([Values(1, 2, 31, 64)] int keyLength, [Values] bool inPlace)
    {
        byte[] value = Bytes(40, 0x11);
        byte[] newKey = Nibbles(keyLength - 1);
        TrieNode leaf = DecodedLeaf(Nibbles(keyLength), value);
        if (inPlace)
        {
            leaf = leaf.Unseal();
            leaf.Key = newKey;
        }
        else
        {
            leaf = leaf.CloneWithChangedKey(newKey);
        }

        Assert.That(Encode(leaf), Is.EqualTo(Encode(TrieNodeFactory.CreateLeaf(newKey, new CappedArray<byte>(value)))));
    }

    /// <summary>A sealed leaf decoded from RLP, as the guest reads one from its witness.</summary>
    private static TrieNode DecodedLeaf(byte[] key, byte[] value)
    {
        TrieNode leaf = new(NodeType.Unknown, Encode(TrieNodeFactory.CreateLeaf(key, new CappedArray<byte>(value))));
        leaf.ResolveNode(NullTrieNodeResolver.Instance, TreePath.Empty);
        return leaf;
    }

    private static byte[] Encode(TrieNode node)
    {
        TreePath path = TreePath.Empty;
        return node.RlpEncode(NullTrieNodeResolver.Instance, ref path).AsSpan().ToArray();
    }

    private static byte[] Nibbles(int length)
    {
        byte[] nibbles = new byte[length];
        for (int i = 0; i < length; i++) nibbles[i] = (byte)((i * 7 + 3) & 0xF);
        return nibbles;
    }

    private static byte[] Bytes(int length, byte fill)
    {
        byte[] bytes = new byte[length];
        bytes.AsSpan().Fill(fill);
        return bytes;
    }
}
