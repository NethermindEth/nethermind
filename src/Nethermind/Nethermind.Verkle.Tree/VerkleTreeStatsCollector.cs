// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Verkle.Tree.Serializers;
using Nethermind.Verkle.Tree.TreeNodes;

namespace Nethermind.Verkle.Tree;

public class VerkleTreeStatsCollector : IVerkleTreeVisitor
{
    private readonly ILogger _logger;
    private int _lastAccountNodeCount;

    public VerkleTreeStatsCollector(IKeyValueStore codeKeyValueStore, ILogManager logManager)
    {
        _logger = logManager.GetClassLogger();
    }

    private static InternalNodeSerializer Serializer => InternalNodeSerializer.Instance;

    public VerkleTreeStats Stats { get; } = new();

    public bool IsFullDbScan => true;

    public bool ShouldVisit(byte[] nextNode)
    {
        return true;
    }

    public void VisitTree(Hash256 rootHash, TrieVisitContext trieVisitContext)
    {
    }

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
