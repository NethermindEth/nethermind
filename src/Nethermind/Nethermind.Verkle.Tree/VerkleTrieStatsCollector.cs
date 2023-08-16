// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Verkle;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Verkle.Tree.Interfaces;
using Nethermind.Verkle.Tree.Nodes;
using Nethermind.Verkle.Tree.Serializers;

namespace Nethermind.Verkle.Tree;

public class VerkleTrieStatsCollector: IVerkleTreeVisitor
{
    private static InternalNodeSerializer Serializer => InternalNodeSerializer.Instance;
    private int _lastAccountNodeCount = 0;

    private readonly ILogger _logger;
    public VerkleTrieStatsCollector(IKeyValueStore codeKeyValueStore, ILogManager logManager)
    {
        _logger = logManager.GetClassLogger();
    }

    public VerkleTrieStats Stats { get; } = new();

    public bool IsFullDbScan => true;

    public bool ShouldVisit(byte[] nextNode)
    {
        return true;
    }

    public void VisitTree(VerkleCommitment rootHash, TrieVisitContext trieVisitContext) { }

    public void VisitMissingNode(byte[] nodeKey, TrieVisitContext trieVisitContext)
    {
        Interlocked.Increment(ref Stats._missingLeaf);
        Interlocked.Increment(ref Stats._stateLevels[trieVisitContext.Level]);
    }

    public void VisitBranchNode(InternalNode node, TrieVisitContext trieVisitContext)
    {
        Interlocked.Add(ref Stats._stateSize, Serializer.GetLength(node, RlpBehaviors.None));
        Interlocked.Increment(ref Stats._stateBranchCount);

        Interlocked.Increment(ref Stats._stateLevels[trieVisitContext.Level]);
    }

    public void VisitStemNode(InternalNode node, TrieVisitContext trieVisitContext)
    {
        Interlocked.Add(ref Stats._stateSize, Serializer.GetLength(node, RlpBehaviors.None));
        Interlocked.Increment(ref Stats._stateStemCount);

        Interlocked.Increment(ref Stats._stateLevels[trieVisitContext.Level]);
    }

    public void VisitLeafNode(ReadOnlySpan<byte> nodeKey, TrieVisitContext trieVisitContext, byte[]? nodeValue)
    {
        if (Stats.StateCount - _lastAccountNodeCount > 1_000_000)
        {
            _lastAccountNodeCount = Stats.StateCount;
            _logger.Warn($"Collected info from {Stats.StateCount} nodes. Missing LEAF {Stats.MissingLeaf}");
        }

        Interlocked.Add(ref Stats._stateSize, 32);
        Interlocked.Increment(ref Stats._stateLeafCount);

        Interlocked.Increment(ref Stats._stateLevels[trieVisitContext.Level]);
    }
}
