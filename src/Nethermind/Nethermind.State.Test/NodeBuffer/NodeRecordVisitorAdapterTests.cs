// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;
using NUnit.Framework;

namespace Nethermind.Store.Test.NodeBuffer;

internal class NodeRecordVisitorAdapterTests
{
    private static IEnumerable<TestCaseData> LeafCases()
    {
        yield return new TestCaseData("c482201205", true).SetName("embedded leaf");
        yield return new TestCaseData("c482201205", false).SetName("hashed leaf");
        yield return new TestCaseData("f90450" + "20" + "b9044c" + string.Concat(Enumerable.Repeat("ab", 1100)), false).SetName("hashed leaf longer than the stack buffer");
    }

    [TestCaseSource(nameof(LeafCases))]
    public void Hands_the_record_visitor_the_node_with_a_zero_hash_only_when_embedded(string rlpHex, bool embedded)
    {
        byte[] rlp = Bytes.FromHexString(rlpHex);
        Hash256 hash = Keccak.Compute(rlp);
        TrieNode node = embedded ? new TrieNode(NodeType.Unknown, rlp) : new TrieNode(NodeType.Unknown, hash, new CappedArray<byte>(rlp));
        LeafCollector collector = new();

        new NodeRecordVisitorAdapter<EmptyContext>(collector).VisitLeaf(default, node);

        (ValueHash256 Hash, byte[] Rlp) leaf = collector.Leaves.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(leaf.Hash, Is.EqualTo(embedded ? ValueKeccak.Zero : hash.ValueHash256));
            Assert.That(leaf.Rlp, Is.EqualTo(rlp));
        }
    }

    private sealed class LeafCollector : INodeRecordVisitor<EmptyContext>
    {
        public List<(ValueHash256 Hash, byte[] Rlp)> Leaves { get; } = [];
        public bool IsFullDbScan => false;
        public bool IsRangeScan => false;
        public bool ShouldVisit(in EmptyContext nodeContext, in ValueHash256 nextNode) => true;
        public void VisitTree(in EmptyContext nodeContext, in ValueHash256 rootHash) { }
        public void VisitMissingNode(in EmptyContext nodeContext, in ValueHash256 nodeHash) { }
        public void VisitBranch(in EmptyContext nodeContext, NodeRecordRef node) { }
        public void VisitExtension(in EmptyContext nodeContext, NodeRecordRef node) { }
        public void VisitLeaf(in EmptyContext nodeContext, NodeRecordRef node) => Leaves.Add((node.Hash, node.Rlp.ToArray()));
        public void VisitAccount(in EmptyContext nodeContext, NodeRecordRef node, in AccountStruct account) { }
    }
}
