// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.NodeBuffer;

internal class TreeVisitorAdapterTests
{
    [Test]
    public void Hands_the_tree_visitor_a_hashless_node_only_for_an_embedded_record([Values] bool embedded)
    {
        byte[] rlp = Bytes.FromHexString("c482201205");
        ValueHash256 hash = embedded ? ValueKeccak.Zero : ValueKeccak.Compute(rlp);
        using PrivateNodeRecordBuffer buffer = new(4096);
        buffer.TryLocate(buffer.AppendRecordFromRlp(null, TreePath.Empty, hash, rlp, []), out NodeRecord record);
        LeafCollector collector = new();

        new TreeVisitorAdapter<EmptyContext>(collector).VisitLeaf(default, record.View);

        TrieNode leaf = collector.Leaves.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(leaf.IsLeaf, Is.True);
            Assert.That(leaf.Keccak, Is.EqualTo(embedded ? null : new Hash256(hash)));
            Assert.That(leaf.FullRlp.ToArray(), Is.EqualTo(rlp));
        }
    }

    private sealed class LeafCollector : ITreeVisitor<EmptyContext>
    {
        public List<TrieNode> Leaves { get; } = [];
        public bool IsFullDbScan => false;
        public bool ShouldVisit(in EmptyContext nodeContext, in ValueHash256 nextNode) => true;
        public void VisitTree(in EmptyContext nodeContext, in ValueHash256 rootHash) { }
        public void VisitMissingNode(in EmptyContext nodeContext, in ValueHash256 nodeHash) { }
        public void VisitBranch(in EmptyContext nodeContext, TrieNode node) { }
        public void VisitExtension(in EmptyContext nodeContext, TrieNode node) { }
        public void VisitLeaf(in EmptyContext nodeContext, TrieNode node) => Leaves.Add(node);
        public void VisitAccount(in EmptyContext nodeContext, TrieNode node, in AccountStruct account) { }
    }
}
