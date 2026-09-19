// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.Intrinsics.X86;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.History.Proofs;
using Nethermind.State.Flat.History.Walk;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class NodeViewsTests
{
    [Test]
    public void Batched_child_views_match_individual_decoding([Range(0, 16)] int fullBranches, [Values] bool interleaved)
    {
        byte[]?[] rlps = new byte[]?[BranchRlp.ChildCount];
        NodeView[] expected = new NodeView[BranchRlp.ChildCount];
        NodeView[] actual = new NodeView[BranchRlp.ChildCount];
        Random random = new(1234);
        ChildVector references = ChildVector.Rent();
        try
        {
            for (int i = 0; i < rlps.Length; i++)
            {
                int index = interleaved ? i * 7 % rlps.Length : i;
                if (i < fullBranches)
                {
                    for (int child = 0; child < BranchRlp.ChildCount; child++)
                    {
                        byte[] hash = new byte[Hash256.Size];
                        random.NextBytes(hash);
                        references.SetHash(child, new ValueHash256(hash));
                    }
                    rlps[index] = BranchRlp.Encode(references);
                }
                else if ((i & 1) == 0)
                {
                    NodeView leaf = NodeView.Leaf([1, 2, 3], [(byte)i]);
                    rlps[index] = leaf.Rlp.ToArray();
                    leaf.Release();
                }
            }

            for (int i = 0; i < rlps.Length; i++) expected[i] = rlps[i] is { } rlp ? NodeViews.FromRlp(rlp) : NodeView.Empty;
            long before = NodeViews.BatchedBranchHashes;
            NodeViews.FromChildrenRlp(rlps, actual);
            int expectedBatched = Avx512F.IsSupported ? fullBranches - (fullBranches % 8 < 3 ? fullBranches % 8 : 0)
                : Avx2.IsSupported ? fullBranches / 4 * 4 : 0;
            Assert.That(NodeViews.BatchedBranchHashes - before, Is.EqualTo(expectedBatched), "child views hashed by SIMD");
            for (int i = 0; i < rlps.Length; i++)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(actual[i].Kind, Is.EqualTo(expected[i].Kind));
                    ValueHash256 expectedHash = rlps[i] is { } encoded ? ValueKeccak.Compute(encoded) : Keccak.EmptyTreeHash.ValueHash256;
                    Assert.That(actual[i].Hash, Is.EqualTo(expectedHash));
                    Assert.That(actual[i].Rlp.ToArray(), Is.EqualTo(rlps[i] ?? []));
                }
            }
            NodeView expectedParent = NodeViews.Combine(expected);
            NodeView actualParent = NodeViews.Combine(actual);
            try
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(actualParent.Rlp.ToArray(), Is.EqualTo(expectedParent.Rlp.ToArray()));
                    Assert.That(actualParent.Hash, Is.EqualTo(expectedParent.Hash));
                }
                VerifyArchiveComposition(rlps, expectedParent, interleaved ? 4 : 1);
            }
            finally
            {
                actualParent.Release();
                expectedParent.Release();
            }
        }
        finally
        {
            foreach (NodeView view in actual) view.Release();
            foreach (NodeView view in expected) view.Release();
            ChildVector.Return(references);
        }
    }

    private static void VerifyArchiveComposition(byte[]?[] rlps, NodeView expected, int fanOut)
    {
        using SnapshotableMemDb rows = new();
        using SnapshotableMemDb commitmentRows = new();
        using SnapshotableMemDb availability = new();
        HistoryRowFormat format = HistoryRowFormat.Resolve(new HistoryAvailability(availability), new FlatDbConfig());
        CommitmentDepthPolicy policy = CommitmentDepthPolicy.Default;
        CommitmentStore commitments = new(commitmentRows, policy, 0);
        TreePath parent = TreePath.FromHexString("a");
        using (IWriteBatch batch = commitmentRows.StartWriteBatch())
        {
            for (int index = 0; index < rlps.Length; index++)
            {
                if (rlps[index] is not { } rlp) continue;
                byte[] prefix = new byte[CommitmentKeyLayout.MaxKeyLength];
                int prefixLength = CommitmentKeyLayout.WritePathPrefix(prefix, parent.Append(index), exact: true);
                byte[] row = new byte[ParentRowCodec.WholeNodeRowLength(rlp.Length)];
                int rowLength = ParentRowCodec.EncodeWholeNode(1, rlp, row);
                commitments.Write(prefix.AsSpan(0, prefixLength), 1, row.AsSpan(0, rowLength), batch);
            }
        }

        HistoricalTrieNodeBuilder builder = new(new AccountHistoryScope(rows, format, commitments, policy), 1, new ResolutionBudget(0), fanOut, new ArchiveProofNodeCache(100));
        Hash256 hash = expected.Hash.ToCommitment();
        Assert.That(builder.LoadRlp(parent, hash), Is.EqualTo(expected.Rlp.ToArray()));
        Assert.That(builder.LoadRlp(parent, hash), Is.EqualTo(expected.Rlp.ToArray()));
        Assert.That(() => builder.LoadRlp(parent, Keccak.EmptyTreeHash), Throws.InstanceOf<StateUnavailableException>());
    }

    [Test]
    public void Batched_child_views_reject_malformed_rlp()
    {
        byte[]?[] children = new byte[]?[BranchRlp.ChildCount];
        NodeView[] views = new NodeView[BranchRlp.ChildCount];
        for (int i = 0; i < 8; i++) children[i] = new byte[532];
        try
        {
            Assert.That(() => NodeViews.FromChildrenRlp(children, views), Throws.InstanceOf<Serialization.Rlp.RlpException>());
        }
        finally
        {
            foreach (NodeView view in views) view.Release();
        }
    }

    [TestCase(1, 1)]
    [TestCase(2, 1)]
    [TestCase(3, 1)]
    [TestCase(17, 1)]
    [TestCase(40, 2)]
    [TestCase(300, 2)]
    [TestCase(3000, 3)]
    public void Subtree_views_combined_upward_reproduce_the_whole_tries_root(int accounts, int partitionDepth)
    {
        Random random = new(accounts * 31 + partitionDepth);
        List<(ValueHash256 Path, Account Account)> leaves = [];
        for (int i = 0; i < accounts; i++)
        {
            byte[] path = new byte[Hash256.Size];
            random.NextBytes(path);
            leaves.Add((new ValueHash256(path), new Account((ulong)i, (UInt256)(1000 + i))));
        }

        StateTree whole = new(new RawScopedTrieStore(new MemDb()), LimboLogs.Instance);
        foreach ((ValueHash256 path, Account account) in leaves) whole.Set(path, account);
        whole.UpdateRootHash();

        NodeView combined = CombineLevel(leaves, TreePath.Empty, partitionDepth);

        Assert.That(combined.Hash, Is.EqualTo(whole.RootHash.ValueHash256),
            "every partition's view, combined nibble by nibble up to the root, must hash to exactly what the full trie hashes to");
        combined.Release();
    }

    [Test]
    public void An_empty_partition_set_combines_to_the_empty_view()
    {
        NodeView[] children = new NodeView[BranchRlp.ChildCount];
        Array.Fill(children, NodeView.Empty);

        NodeView combined = NodeViews.Combine(children);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(combined.Kind, Is.EqualTo(NodeViewKind.Empty));
            Assert.That(combined.Hash, Is.EqualTo(Keccak.EmptyTreeHash.ValueHash256));
        }
    }

    private static NodeView CombineLevel(List<(ValueHash256 Path, Account Account)> leaves, in TreePath prefix, int partitionDepth)
    {
        if (prefix.Length == partitionDepth)
        {
            RawScopedTrieStore store = new(new MemDb());
            StateTree partial = new(store, LimboLogs.Instance);
            foreach ((ValueHash256 path, Account account) in leaves)
            {
                if (HasPrefix(path, prefix)) partial.Set(path, account);
            }

            partial.UpdateRootHash();
            return NodeViews.FromRoot(partial.RootRef, prefix.Length, store);
        }

        NodeView[] children = new NodeView[BranchRlp.ChildCount];
        for (int nibble = 0; nibble < BranchRlp.ChildCount; nibble++) children[nibble] = CombineLevel(leaves, prefix.Append(nibble), partitionDepth);
        NodeView combined = NodeViews.Combine(children);
        foreach (NodeView child in children) child.Release();
        return combined;
    }

    private static bool HasPrefix(in ValueHash256 path, in TreePath prefix)
    {
        for (int i = 0; i < prefix.Length; i++)
        {
            byte value = path.Bytes[i / 2];
            int nibble = (i & 1) == 0 ? value >> 4 : value & 0x0F;
            if (nibble != prefix[i]) return false;
        }

        return true;
    }
}
