// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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
        return NodeViews.Combine(children);
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
