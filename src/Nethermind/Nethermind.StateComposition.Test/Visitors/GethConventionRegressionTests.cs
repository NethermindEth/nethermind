// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;
using Nethermind.StateComposition.Data;
using Nethermind.StateComposition.Visitors;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.StateComposition.Test.Visitors;

/// <summary>
/// Geth convention regression tests — verify all 5 conventions that ensure
/// parity with Geth's inspect-trie output format.
/// Convention 1: Short = Extension + Leaf (Geth shortNode wraps both)
/// Convention 2: ValueNodeCount at depth i = leaves physically at depth i-1
/// Convention 3: MaxDepth = raw depth + 1 (Geth counts embedded valueNode)
/// Convention 4: TotalNodes = physicalNodes + valueNodes (double-count leaves)
/// Convention 5: StorageMaxDepthHistogram bucket = raw depth + 1
/// </summary>
[TestFixture]
public class GethConventionRegressionTests
{
    [Test]
    public void Convention1_ShortNode_EqualsExtensionPlusLeaf_AccountTrie()
    {
        using StateCompositionVisitor visitor = new(LimboLogs.Instance);

        // Valid empty-branch RLP (17 × 0x80) so IsChildNull can decode without throwing.
        byte[] emptyBranchRlp = new byte[18]; emptyBranchRlp[0] = 0xD1; for (int i = 1; i <= 17; i++) emptyBranchRlp[i] = 0x80;
        TrieNode branchNode = new(NodeType.Branch, emptyBranchRlp);
        TrieNode extNode = new(NodeType.Extension, [0xc0, 0x01]);
        TrieNode leafNode = new(NodeType.Leaf, [0xc0, 0x01]);

        StateCompositionContext ctx0 = new(default, level: 0, isStorage: false, branchChildIndex: null);
        StateCompositionContext ctx1 = new(default, level: 1, isStorage: false, branchChildIndex: null);
        StateCompositionContext ctx2 = new(default, level: 2, isStorage: false, branchChildIndex: null);

        visitor.VisitBranch(in ctx0, branchNode);  // 1 full node
        visitor.VisitExtension(in ctx1, extNode);   // 1 extension (short)
        visitor.VisitLeaf(in ctx2, leafNode);        // 1 leaf (also counted as short in Geth)

        StateCompositionStats stats = visitor.GetStats(1, null);

        using (Assert.EnterMultipleScope())
        {
            // Convention 1: ShortNodes = Extensions + Leaves = 1 + 1 = 2
            Assert.That(stats.AccountTrieShortNodes, Is.EqualTo(2),
                "C1: AccountTrieShortNodes = extensions(1) + leaves(1)");
            Assert.That(stats.AccountTrieValueNodes, Is.EqualTo(1),
                "C1: AccountTrieValueNodes = leaves(1)");
            Assert.That(stats.AccountTrieFullNodes, Is.EqualTo(1),
                "C1: AccountTrieFullNodes = branches(1)");
        }
    }

    [Test]
    public void Convention2_ValueNodeCount_ShiftedByOne_AccountTrie()
    {
        using StateCompositionVisitor visitor = new(LimboLogs.Instance);

        TrieNode leafNode = new(NodeType.Leaf, [0xc0, 0x01]);

        // Place 2 leaves at depth 3
        StateCompositionContext ctx3 = new(default, level: 3, isStorage: false, branchChildIndex: null);
        visitor.VisitLeaf(in ctx3, leafNode);
        visitor.VisitLeaf(in ctx3, leafNode);

        TrieDepthDistribution dist = visitor.GetTrieDistribution();

        // Depth 3: ShortNodeCount=2 (leaves), ValueNodeCount=0 (shifted from depth 2 which has 0)
        // Depth 4: ShortNodeCount=0, ValueNodeCount=2 (shifted from depth 3 which has 2 leaves)
        TrieLevelStat? depth3 = null, depth4 = null;
        foreach (TrieLevelStat ls in dist.AccountTrieLevels)
        {
            switch (ls.Depth)
            {
                case 3:
                    depth3 = ls;
                    break;
                case 4:
                    depth4 = ls;
                    break;
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(depth3, Is.Not.Null, "Depth 3 should have physical nodes");
            Assert.That(depth3.Value.ShortNodeCount, Is.EqualTo(2),
                "C2: Depth 3 ShortNodeCount = 2 leaves (as short nodes)");
            Assert.That(depth3.Value.ValueNodeCount, Is.Zero,
                "C2: Depth 3 ValueNodeCount = 0 (no leaves at depth 2)");

            Assert.That(depth4, Is.Not.Null, "Depth 4 should exist due to value shift");
            Assert.That(depth4.Value.ValueNodeCount, Is.EqualTo(2),
                "C2: Depth 4 ValueNodeCount = 2 (shifted from depth 3 leaves)");
        }
    }

    /// <summary>
    /// Comprehensive test: all 5 conventions verified on a single storage trie
    /// with known structure: branch@0, extension@1, 3 leaves@2.
    /// </summary>
    [Test]
    public void AllConventions_StorageTrie_Comprehensive()
    {
        VisitorCounters c = new();

        c.BeginStorageTrie(default, default);
        c.TrackStorageNode(depth: 0, byteSize: 100, isLeaf: false, isBranch: true);  // branch
        c.TrackStorageNode(depth: 1, byteSize: 50, isLeaf: false, isBranch: false);  // extension
        c.TrackStorageNode(depth: 2, byteSize: 30, isLeaf: true, isBranch: false);   // leaf
        c.TrackStorageNode(depth: 2, byteSize: 30, isLeaf: true, isBranch: false);   // leaf
        c.TrackStorageNode(depth: 2, byteSize: 30, isLeaf: true, isBranch: false);   // leaf
        c.Flush();

        TopContractEntry entry = c.TopN.TopByDepth[0];

        using (Assert.EnterMultipleScope())
        {
            // Summary.ShortNodeCount = extensions(1) + leaves(3) = 4
            Assert.That(entry.Summary.ShortNodeCount, Is.EqualTo(4),
                "C1: ShortNodeCount = extension + leaf");
            Assert.That(entry.Summary.FullNodeCount, Is.EqualTo(1),
                "C1: FullNodeCount = branches only");

            // Levels[3].ValueNodeCount = leaves at depth 2 = 3 (shifted +1)
            Assert.That(entry.Levels[3].ValueNodeCount, Is.EqualTo(3),
                "C2: ValueNodeCount at depth 3 = leaves physically at depth 2");
            Assert.That(entry.Levels[2].ValueNodeCount, Is.Zero,
                "C2: ValueNodeCount at depth 2 = 0 (no leaves at depth 1)");

            // MaxDepth = raw(2) + 1 = 3
            Assert.That(entry.MaxDepth, Is.EqualTo(3),
                "C3: MaxDepth = raw depth + 1");

            // TotalNodes = physical(5) + value(3) = 8
            Assert.That(entry.TotalNodes, Is.EqualTo(8),
                "C4: TotalNodes double-counts leaves");

            // Histogram at bucket 3 (raw 2 + 1)
            Assert.That(c.StorageMaxDepthHistogram[3], Is.EqualTo(1),
                "C5: Histogram bucket = raw depth + 1");
        }
    }

}
