// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Intrinsics.X86;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

/// <summary>
/// Covers <see cref="DirtyNodeHasher" />, which hashes a commit's dirty nodes in batches rather
/// than one at a time.
/// </summary>
/// <remarks>
/// Every case checks each node's digest against <see cref="Keccak.Compute(ReadOnlySpan{byte})" />
/// rather than against a second run of the same code. Comparing two runs would only prove the
/// batching is deterministic; a wrong lane, a wrong padded length or a kernel narrower than the
/// group would be identical on both sides and pass.
/// </remarks>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class DirtyNodeHasherTests
{
    /// <summary>Value lengths chosen so the leaves they produce land in each class the code treats
    /// differently: embedded, the four padded lengths the kernels take, and one past them.</summary>
    private static readonly int[] ValueLengths = [1, 40, 150, 300, 450, 600];

    /// <summary>Entry counts that put group sizes below, at and above both kernel widths.</summary>
    private static readonly int[] EntryCounts = [1, 2, 3, 4, 5, 9, 40, 200];

    [Test]
    public void Every_node_hash_matches_a_scalar_keccak(
        [ValueSource(nameof(EntryCounts))] int entries,
        [ValueSource(nameof(ValueLengths))] int valueLength,
        [Values] bool canBeParallel)
    {
        PatriciaTree tree = BuildDirtyTree(entries, valueLength, ScatteredKey);
        tree.UpdateRootHash(canBeParallel);

        AssertEveryNodeHashMatchesScalarKeccak(tree, entries);
    }

    [Test]
    public void Short_keys_leave_nodes_embedded_in_their_parent([Values] bool canBeParallel)
    {
        // A one-byte value under a two-byte key encodes below thirty-two bytes, so the parent
        // carries the node itself rather than a hash and the level order must not hash it. A trie
        // keyed by a 32-byte hash never reaches that, because the path alone is longer than that.
        PatriciaTree tree = new(new RawScopedTrieStore(new MemDb()), NullLogManager.Instance);
        for (int i = 0; i < 40; i++)
        {
            tree.Set([(byte)i, (byte)(i * 7)], [(byte)(i + 1)]);
        }

        tree.UpdateRootHash(canBeParallel);

        AssertEveryNodeHashMatchesScalarKeccak(tree, minimumHashed: 1, minimumEmbedded: 1);
    }

    [Test]
    public void Shared_prefixes_put_extension_nodes_on_the_path(
        [Values(2, 5, 9, 40)] int entries,
        [Values] bool canBeParallel)
    {
        // Keys that agree for their first thirty nibbles force an extension above the branch that
        // separates them, so the level order has to account for a child deeper than one nibble.
        PatriciaTree tree = BuildDirtyTree(entries, valueLength: 40, SharedPrefixKey);
        tree.UpdateRootHash(canBeParallel);

        AssertEveryNodeHashMatchesScalarKeccak(tree, entries);
    }

    [Test]
    public void Mixed_value_lengths_split_one_level_across_padded_classes([Values] bool canBeParallel)
    {
        // One length class per leaf in rotation, so a level ends holding partly filled groups of
        // several classes at once and each has to be flushed before the parents are encoded.
        PatriciaTree tree = BuildDirtyTree(120, valueLength: 0, ScatteredKey,
            valueLengthFor: i => ValueLengths[i % ValueLengths.Length]);
        tree.UpdateRootHash(canBeParallel);

        AssertEveryNodeHashMatchesScalarKeccak(tree, 120);
    }

    /// <remarks>Compares this trie's hash and RLP instances, not the process-wide hash counter, which
    /// tests running at the same time also increment.</remarks>
    [Test]
    public void Second_call_rehashes_nothing_and_leaves_the_root_alone([Values] bool canBeParallel)
    {
        PatriciaTree tree = BuildDirtyTree(40, valueLength: 40, ScatteredKey);
        tree.UpdateRootHash(canBeParallel);
        Hash256 first = tree.RootHash;
        List<(TrieNode Node, Hash256? Keccak, byte[]? Rlp)> afterFirst = SnapshotHashes(tree.RootRef!);

        tree.UpdateRootHash(canBeParallel);

        // Hashing or encoding a node again allocates a new digest or buffer, so equal values are not enough.
        int redone = afterFirst.Count(n => !ReferenceEquals(n.Node.Keccak, n.Keccak) || !ReferenceEquals(n.Node.FullRlp.UnderlyingArray, n.Rlp));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(redone, Is.Zero, "a clean trie must not be encoded or hashed again");
            Assert.That(tree.RootHash, Is.EqualTo(first));
            AssertEveryNodeHashMatchesScalarKeccak(tree, 40);
        }
    }

    [Test]
    public void A_budget_too_small_for_the_subtree_hashes_what_it_collected([Values(1, 7, 60)] int budget)
    {
        PatriciaTree tree = BuildDirtyTree(200, valueLength: 40, ScatteredKey);

        bool handled = DirtyNodeHasher.HashBelowRoot(tree.RootRef!, tree.TrieStore, null,
            canBeParallel: false, maxCollectedNodes: budget);

        Assert.That(handled, Is.False, "a budget this small cannot cover a two-hundred entry trie");
        Assert.That(tree.RootRef!.Keccak, Is.Null, "the root is the caller's to hash");
        // Below the minimum a level order is not worth it; above it, discarding the collection would
        // leave a bulk commit paying for the walk and getting no batching. Without AVX2 there is no level order.
        int hashed = CountHashedBelow(tree.RootRef!);
        if (budget < 4 || !Avx2.IsSupported) Assert.That(hashed, Is.Zero, "nothing is hashed below the minimum or without AVX2");
        else Assert.That(hashed, Is.GreaterThanOrEqualTo(budget), "the collected nodes must be hashed, not thrown away");

        // Stopping part way is only safe if the ordinary walk still produces the same trie.
        tree.UpdateRootHash();
        AssertEveryNodeHashMatchesScalarKeccak(tree, 200);
    }

    [Test]
    public void The_order_never_puts_a_node_before_one_of_its_descendants([Values(5, 40, 200)] int entries)
    {
        // Reversing this order still produces the right root, because encoding a parent falls back
        // to hashing its children itself, so only reading the order can hold it. Getting it wrong
        // costs the batching the whole type exists for.
        PatriciaTree tree = BuildDirtyTree(entries, valueLength: 40, ScatteredKey);

        int[] pathLengths = DirtyNodeHasher.DeepestFirstPathLengths(tree.RootRef!);

        Assert.That(pathLengths, Is.Ordered.Descending, "a node must come before every ancestor of it");
        Assert.That(pathLengths, Has.Length.GreaterThan(entries), "the collection walk missed nodes");
    }

    [Test]
    public void A_clean_root_is_left_alone()
    {
        PatriciaTree tree = BuildDirtyTree(40, valueLength: 40, ScatteredKey);
        tree.UpdateRootHash();

        Assert.That(DirtyNodeHasher.HashBelowRoot(tree.RootRef!, tree.TrieStore, null, canBeParallel: true),
            Is.False, "a root that already carries a hash has nothing below it left to do");
    }

    private static List<(TrieNode Node, Hash256? Keccak, byte[]? Rlp)> SnapshotHashes(TrieNode root)
    {
        List<(TrieNode, Hash256?, byte[]?)> nodes = [];
        Collect(root);
        return nodes;

        void Collect(TrieNode node)
        {
            nodes.Add((node, node.Keccak, node.FullRlp.UnderlyingArray));
            int childCount = ChildCount(node);
            for (int i = 0; i < childCount; i++)
            {
                if (node.TryGetDirtyChild(i, out TrieNode? child)) Collect(child);
            }
        }
    }

    private static int CountHashedBelow(TrieNode node)
    {
        int hashed = 0;
        int childCount = ChildCount(node);
        for (int i = 0; i < childCount; i++)
        {
            if (!node.TryGetDirtyChild(i, out TrieNode? child)) continue;
            if (child.Keccak is not null) hashed++;
            hashed += CountHashedBelow(child);
        }

        return hashed;
    }

    /// <param name="minimumHashed">Every entry of a trie keyed by a 32-byte hash is a leaf whose path
    /// remainder alone is longer than a hash, so the entry count is a lower bound there.</param>
    private static void AssertEveryNodeHashMatchesScalarKeccak(PatriciaTree tree, int minimumHashed, int minimumEmbedded = 0)
    {
        (int hashed, int embedded) = AssertSubtree(tree.RootRef!, isRoot: true);

        // Without these the assertions above would also pass on a trie the walk never visited.
        Assert.That(hashed, Is.GreaterThanOrEqualTo(minimumHashed),
            "the walk found too few hashed nodes to have proved anything");
        Assert.That(embedded, Is.GreaterThanOrEqualTo(minimumEmbedded),
            "the walk found no embedded node, so it did not cover that case");
    }

    private static (int Hashed, int Embedded) AssertSubtree(TrieNode node, bool isRoot)
    {
        int hashed = 0;
        int embedded = 0;
        CappedArray<byte> rlp = node.FullRlp;
        if (rlp.IsNotNull)
        {
            if (rlp.Length >= Hash256.Size || isRoot)
            {
                Assert.That(node.Keccak, Is.EqualTo(Keccak.Compute(rlp.AsSpan())),
                    $"a {node.NodeType} of {rlp.Length} bytes got the wrong digest");
                hashed++;
            }
            else
            {
                Assert.That(node.Keccak, Is.Null, "a node shorter than a hash is embedded in its parent");
                embedded++;
            }
        }

        int childCount = ChildCount(node);
        for (int i = 0; i < childCount; i++)
        {
            if (node.TryGetDirtyChild(i, out TrieNode? child))
            {
                (int childHashed, int childEmbedded) = AssertSubtree(child, isRoot: false);
                hashed += childHashed;
                embedded += childEmbedded;
            }
        }

        return (hashed, embedded);
    }

    /// <summary>Child slots to ask about; a leaf has none, and asking one throws.</summary>
    private static int ChildCount(TrieNode node) => node.IsBranch ? 16 : node.IsExtension ? 1 : 0;

    private static PatriciaTree BuildDirtyTree(int entries, int valueLength, Func<int, byte[]> key,
        Func<int, int>? valueLengthFor = null)
    {
        PatriciaTree tree = new(new RawScopedTrieStore(new MemDb()), NullLogManager.Instance);
        for (int i = 0; i < entries; i++)
        {
            tree.Set(key(i), Value(i, valueLengthFor?.Invoke(i) ?? valueLength));
        }

        return tree;
    }

    /// <summary>A key whose nibbles differ from the first, so the entries spread across the trie.</summary>
    private static byte[] ScatteredKey(int i) => Keccak.Compute(BitConverter.GetBytes(i)).BytesToArray();

    /// <summary>A key sharing fifteen leading bytes with every other, which forces an extension.</summary>
    private static byte[] SharedPrefixKey(int i)
    {
        byte[] bytes = new byte[32];
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(15), i * 7 + 1);
        return bytes;
    }

    private static byte[] Value(int i, int length)
    {
        byte[] value = new byte[length];
        value.AsSpan().Fill((byte)(i + 1));
        return value;
    }
}
