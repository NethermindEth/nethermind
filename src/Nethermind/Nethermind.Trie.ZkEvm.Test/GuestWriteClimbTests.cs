// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.ZkEvm.Test;

/// <summary>Tests for the guest's trie write, which unseals every ancestor of the first level it changes at once.</summary>
/// <remarks>
/// The trie starts sealed and hashed, as nodes read from the witness are. After each write, every node on the paths written
/// so far must be pending, dirty and unhashed, as must the parent of every pending node, so the root computation re-hashes
/// them; every other node keeps its hash. Not parallel: the guest's trie code assumes a single thread. Nothing is hashed:
/// in a ZK_EVM build the Keccak permutation is a zkVM precompile that throws outside the guest.
/// </remarks>
[NonParallelizable]
public class GuestWriteClimbTests
{
    private static readonly Hash256 SealedHash = new(new byte[Hash256.Size]);

    public enum Write { Update, Insert, Delete }

    /// <summary>Every pair of writes; only an insert after the first write reads how many nibbles its key shares with it.</summary>
    private static IEnumerable<TestCaseData> WritePairs() =>
        from first in Enum.GetValues<Write>()
        from second in Enum.GetValues<Write>()
        from sharedNibbles in second == Write.Insert ? new[] { 0, 1, 3 } : [0]
        select new TestCaseData(first, second, sharedNibbles);

    [TestCaseSource(nameof(WritePairs))]
    public void Writes_leave_exactly_their_paths_pending(Write first, Write second, int sharedNibbles)
    {
        Dictionary<string, byte[]> expected = [];
        PatriciaTree tree = new();
        for (int i = 0; i < 24; i++) Put(tree, expected, Key(i), [(byte)(i + 1)]);
        // Every key differs in its first byte, so each deleted key and its sibling share a branch under an extension,
        // which the delete collapses.
        Put(tree, expected, Sharing(Key(7), 61, 3), [0xCC]);
        Put(tree, expected, Sharing(Key(9), 62, 3), [0xDD]);
        SealAll(tree.RootRef!);

        List<byte[]> written = [];
        byte[] firstKey = Apply(tree, expected, first, Key(first == Write.Insert ? 50 : 7), 0xAA);
        written.Add(firstKey);
        AssertState(tree, expected, written);

        byte[] secondKey = second == Write.Insert ? Sharing(firstKey, 60, sharedNibbles) : Key(second == Write.Delete ? 9 : 11);
        written.Add(Apply(tree, expected, second, secondKey, 0xBB));
        AssertState(tree, expected, written);
    }

    private static byte[] Key(int seed)
    {
        byte[] key = new byte[32];
        for (int i = 0; i < key.Length; i++) key[i] = (byte)(seed * 73 + i * 11 + 7);
        return key;
    }

    /// <summary>A key whose first <paramref name="nibbles"/> nibbles are those of <paramref name="other"/>, and the next differs.</summary>
    private static byte[] Sharing(byte[] other, int seed, int nibbles)
    {
        byte[] key = Key(seed);
        for (int i = 0; i <= nibbles; i++)
        {
            int shift = i % 2 == 0 ? 4 : 0;
            int nibble = (other[i / 2] >> shift) & 0xF;
            if (i == nibbles) nibble ^= 1;
            key[i / 2] = (byte)(key[i / 2] & ~(0xF << shift) | nibble << shift);
        }

        return key;
    }

    private static byte[] Apply(PatriciaTree tree, Dictionary<string, byte[]> expected, Write write, byte[] key, byte value)
    {
        if (write == Write.Delete)
        {
            tree.Set(key, Array.Empty<byte>());
            expected.Remove(Convert.ToHexString(key));
        }
        else
        {
            Put(tree, expected, key, [value]);
        }

        return key;
    }

    private static void Put(PatriciaTree tree, Dictionary<string, byte[]> expected, byte[] key, byte[] value)
    {
        tree.Set(key, value);
        expected[Convert.ToHexString(key)] = value;
    }

    private static void AssertState(PatriciaTree tree, Dictionary<string, byte[]> expected, List<byte[]> written)
    {
        foreach ((string key, byte[] value) in expected)
        {
            Assert.That(tree.Get(Convert.FromHexString(key)).ToArray(), Is.EqualTo(value), key);
        }

        HashSet<TrieNode> onPaths = [with(ReferenceEqualityComparer.Instance)];
        foreach (byte[] key in written) Walk(tree, key, onPaths);

        foreach ((TrieNode node, TrieNode? parent) in Nodes(tree.RootRef!, null))
        {
            if (onPaths.Contains(node)) Assert.That(IsPending(node), Is.True, $"on a written path: {node}");
            if (IsPending(node) && parent is not null) Assert.That(IsPending(parent), Is.True, $"parent of pending {node}");
            if (!IsPending(node)) Assert.That(node.Keccak, Is.SameAs(SealedHash), $"{node}");
        }
    }

    private static bool IsPending(TrieNode node) => node.Keccak is null && node.IsDirty;

    /// <summary>Adds the nodes from the root towards <paramref name="key"/>, as far as the trie goes, to <paramref name="nodes"/>.</summary>
    private static void Walk(PatriciaTree tree, byte[] key, HashSet<TrieNode> nodes)
    {
        byte[] nibbles = new byte[key.Length * 2];
        Nibbles.BytesToNibbleBytes(key, nibbles);
        TrieNode? node = tree.RootRef;
        int position = 0;
        while (node is not null)
        {
            nodes.Add(node);
            TreePath path = TreePath.Empty;
            if (node.IsBranch)
            {
                node = node.GetChild(tree.TrieStore, ref path, nibbles[position++]);
            }
            else if (node.IsExtension && nibbles.AsSpan(position).StartsWith(node.Key))
            {
                position += node.Key!.Length;
                node = node.GetChild(tree.TrieStore, ref path, 0);
            }
            else
            {
                return;
            }
        }
    }

    private static IEnumerable<(TrieNode Node, TrieNode? Parent)> Nodes(TrieNode node, TrieNode? parent)
    {
        yield return (node, parent);
        int children = node.IsBranch ? TrieNode.BranchesCount : node.IsExtension ? 1 : 0;
        for (int i = 0; i < children; i++)
        {
            TreePath path = TreePath.Empty;
            TrieNode? child = node.GetChild(NullTrieNodeResolver.Instance, ref path, i);
            if (child is null) continue;
            foreach ((TrieNode Node, TrieNode? Parent) descendant in Nodes(child, node)) yield return descendant;
        }
    }

    private static void SealAll(TrieNode root)
    {
        foreach ((TrieNode node, _) in Nodes(root, null).ToList())
        {
            node.Seal();
            node.Keccak = SealedHash;
        }
    }
}
