// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class FlatAddressStorageNodeDictionaryTests
{
    private static readonly Hash256 AddressA = TestItem.AddressA.ToAccountPath.ToCommitment();
    private static readonly Hash256 AddressB = TestItem.AddressB.ToAccountPath.ToCommitment();

    [Test]
    public void Set_and_read_round_trips_rlp()
    {
        FlatAddressStorageNodeDictionary dict = new();
        byte[] rlp = [0xC2, 0x01, 0x02];
        dict[(AddressA, TreePath.FromHexString("12"))] = new TrieNode(NodeType.Leaf, rlp);

        Assert.That(dict.TryGetValue(new((AddressA, TreePath.FromHexString("12"))), out TrieNode? node), Is.True);
        Assert.That(node!.FullRlp.ToArray(), Is.EqualTo(rlp));
    }

    [Test]
    public void Set_and_read_round_trips_keccak_only_as_unknown()
    {
        FlatAddressStorageNodeDictionary dict = new();
        dict[(AddressA, TreePath.Empty)] = new TrieNode(NodeType.Unknown, TestItem.KeccakA);

        Assert.That(dict.TryGetValue(new((AddressA, TreePath.Empty)), out TrieNode? node), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node!.NodeType, Is.EqualTo(NodeType.Unknown));
            Assert.That(node.Keccak, Is.EqualTo(TestItem.KeccakA));
        }
    }

    [Test]
    public void Overwrite_returns_the_latest_record()
    {
        FlatAddressStorageNodeDictionary dict = new();
        TreePath path = TreePath.FromHexString("34");
        dict[(AddressA, path)] = new TrieNode(NodeType.Leaf, new byte[] { 0xC1, 0x01 });
        dict[(AddressA, path)] = new TrieNode(NodeType.Leaf, new byte[] { 0xC1, 0x02 });

        Assert.That(dict.TryGetValue(new((AddressA, path)), out TrieNode? node), Is.True);
        Assert.That(node!.FullRlp.ToArray(), Is.EqualTo(new byte[] { 0xC1, 0x02 }));
    }

    [Test]
    public void Count_sums_across_addresses()
    {
        FlatAddressStorageNodeDictionary dict = new();
        dict[(AddressA, TreePath.FromHexString("12"))] = new TrieNode(NodeType.Leaf, new byte[] { 1 });
        dict[(AddressA, TreePath.FromHexString("34"))] = new TrieNode(NodeType.Leaf, new byte[] { 2 });
        dict[(AddressB, TreePath.FromHexString("56"))] = new TrieNode(NodeType.Leaf, new byte[] { 3 });

        Assert.That(dict.Count, Is.EqualTo(3));
    }

    [Test]
    public void RemoveAddress_drops_only_that_address()
    {
        FlatAddressStorageNodeDictionary dict = new();
        dict[(AddressA, TreePath.FromHexString("12"))] = new TrieNode(NodeType.Leaf, new byte[] { 1 });
        dict[(AddressB, TreePath.FromHexString("56"))] = new TrieNode(NodeType.Leaf, new byte[] { 2 });

        Assert.That(dict.RemoveAddress(AddressA), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dict.Count, Is.EqualTo(1));
            Assert.That(dict.TryGetValue(new((AddressA, TreePath.FromHexString("12"))), out _), Is.False);
            Assert.That(dict.TryGetValue(new((AddressB, TreePath.FromHexString("56"))), out _), Is.True);
        }
    }

    [Test]
    public void Enumeration_yields_every_stored_key()
    {
        FlatAddressStorageNodeDictionary dict = new();
        dict[(AddressA, TreePath.FromHexString("12"))] = new TrieNode(NodeType.Leaf, new byte[] { 1 });
        dict[(AddressA, TreePath.FromHexString("34"))] = new TrieNode(NodeType.Leaf, new byte[] { 2 });
        dict[(AddressB, TreePath.FromHexString("56"))] = new TrieNode(NodeType.Leaf, new byte[] { 3 });

        HashSet<(Hash256, TreePath)> seen = [];
        foreach (KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode> kvp in dict)
        {
            Assert.That(kvp.Value, Is.Not.Null);
            seen.Add(kvp.Key.Key);
        }

        Assert.That(seen, Has.Count.EqualTo(3));
        Assert.That(seen, Does.Contain((AddressA, TreePath.FromHexString("12"))));
        Assert.That(seen, Does.Contain((AddressA, TreePath.FromHexString("34"))));
        Assert.That(seen, Does.Contain((AddressB, TreePath.FromHexString("56"))));
    }

    [Test]
    public void ForEachRlp_visits_raw_spans()
    {
        FlatAddressStorageNodeDictionary dict = new();
        byte[] rlp = [0xC3, 0x0A, 0x0B, 0x0C];
        dict[(AddressA, TreePath.FromHexString("12"))] = new TrieNode(NodeType.Leaf, rlp);

        Dictionary<(Hash256, TreePath), byte[]> visited = [];
        dict.ForEachRlp((in HashedKey<(Hash256, TreePath)> key, bool emptyUnknown, ReadOnlySpan<byte> span) =>
        {
            visited[key.Key] = span.ToArray();
        });

        Assert.That(visited, Has.Count.EqualTo(1));
        Assert.That(visited[(AddressA, TreePath.FromHexString("12"))], Is.EqualTo(rlp));
    }

    [Test]
    public void NoLockClear_empties_and_allows_reuse()
    {
        FlatAddressStorageNodeDictionary dict = new();
        dict[(AddressA, TreePath.FromHexString("12"))] = new TrieNode(NodeType.Leaf, new byte[] { 1 });
        Assert.That(dict.ArenaBytesReserved, Is.GreaterThan(0));

        dict.NoLockClear();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dict.Count, Is.Zero);
            Assert.That(dict.ArenaBytesReserved, Is.Zero);
        }

        byte[] rlp = [0xC1, 0x2A];
        dict[(AddressB, TreePath.FromHexString("56"))] = new TrieNode(NodeType.Leaf, rlp);
        Assert.That(dict.TryGetValue(new((AddressB, TreePath.FromHexString("56"))), out TrieNode? node), Is.True);
        Assert.That(node!.FullRlp.ToArray(), Is.EqualTo(rlp));
    }
}
