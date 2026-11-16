// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.ZkEvm.Test;

[NonParallelizable]
public class GuestLeafValueTests
{
    private static readonly int[] ValueLengths = [1, 2, 32, 33, 55, 56, 70, 110];

    [Test]
    public void Decoded_leaf_value_span_equals_the_encoded_value([ValueSource(nameof(ValueLengths))] int valueLength)
    {
        byte[] value = Bytes(valueLength, 0x5A);
        TrieNode leaf = DecodedLeaf(Key(), value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(leaf.ValueSpan.ToArray(), Is.EqualTo(value));
            Assert.That(((LeafData)leaf.NodeData!).ValueSpan.ToArray(), Is.EqualTo(value));
        }
    }

    [Test]
    public void Value_read_copies_the_value_out_of_the_rlp([ValueSource(nameof(ValueLengths))] int valueLength)
    {
        byte[] value = Bytes(valueLength, 0x5A);
        TrieNode leaf = DecodedLeaf(Key(), value);
        byte[] rlp = leaf.FullRlp.AsSpan().ToArray();

        CappedArray<byte> copied = leaf.Value;
        Assert.That(copied.AsSpan().ToArray(), Is.EqualTo(value));

        copied.AsSpan().Fill(0xFF);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(leaf.FullRlp.AsSpan().ToArray(), Is.EqualTo(rlp));
            Assert.That(leaf.ValueSpan.ToArray(), Is.EqualTo(copied.AsSpan().ToArray()));
            Assert.That(leaf.Value.AsSpan().ToArray(), Is.EqualTo(copied.AsSpan().ToArray()));
        }
    }

    [Test]
    public void Clone_of_a_decoded_leaf_keeps_its_value()
    {
        byte[] value = Bytes(40, 0x33);

        Assert.That(DecodedLeaf(Key(), value).Clone().Value.AsSpan().ToArray(), Is.EqualTo(value));
    }

    [Test]
    public void Value_change_replaces_the_value_read_from_the_rlp()
    {
        byte[] value = Bytes(40, 0x33);
        TrieNode leaf = DecodedLeaf(Key(), value).Unseal();
        byte[] changed = Bytes(41, 0x44);
        leaf.Value = new CappedArray<byte>(changed);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(leaf.ValueSpan.ToArray(), Is.EqualTo(changed));
            Assert.That(leaf.Value.AsSpan().ToArray(), Is.EqualTo(changed));
        }
    }

    [Test]
    public void Raw_key_read_returns_the_value_of_a_decoded_leaf([ValueSource(nameof(ValueLengths))] int valueLength)
    {
        byte[] rawKey = new byte[32];
        for (int i = 0; i < rawKey.Length; i++) rawKey[i] = (byte)(i * 37 + 5);
        byte[] value = Bytes(valueLength, 0x6B);
        TrieNode leaf = DecodedLeaf(Nibbles.BytesToNibbleBytes(rawKey), value);
        PatriciaTree tree = new() { RootRef = leaf };

        Assert.That(tree.Get(rawKey).ToArray(), Is.EqualTo(value));
    }

    private static TrieNode DecodedLeaf(byte[] key, byte[] value)
    {
        TreePath path = TreePath.Empty;
        byte[] rlp = TrieNodeFactory.CreateLeaf(key, new CappedArray<byte>(value)).RlpEncode(NullTrieNodeResolver.Instance, ref path).AsSpan().ToArray();
        TrieNode leaf = new(NodeType.Unknown, rlp);
        leaf.ResolveNode(NullTrieNodeResolver.Instance, TreePath.Empty);
        return leaf;
    }

    private static byte[] Key()
    {
        byte[] nibbles = new byte[63];
        for (int i = 0; i < nibbles.Length; i++) nibbles[i] = (byte)((i * 7 + 3) & 0xF);
        return nibbles;
    }

    private static byte[] Bytes(int length, byte seed)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(seed + i);
        return bytes;
    }
}
