// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Trie;

using Nethermind.StateComposition.Data;

namespace Nethermind.StateComposition.Visitors;

internal sealed class SingleContractVisitor(
    ILogManager logManager,
    ValueHash256 targetStorageRoot,
    CancellationToken ct)
    : ITreeVisitor<StateCompositionContext>, IDisposable
{
    private readonly ILogger _logger = logManager.GetClassLogger<SingleContractVisitor>();

    private bool _collectingTarget;
    private bool _targetCompleted;

    private int _maxDepth;
    private long _totalNodes;
    private long _valueNodes;
    private long _totalSize;
    private DepthCounter16 _depths;

    public bool IsFullDbScan => true;
    public ReadFlags ExtraReadFlag => ReadFlags.HintCacheMiss;
    public bool ExpectAccounts => true;

    public bool ShouldVisit(in StateCompositionContext ctx, in ValueHash256 nextNode)
    {
        if (ct.IsCancellationRequested) return false;
        if (_targetCompleted) return false;
        return !ctx.IsStorage || _collectingTarget;
    }

    public void VisitTree(in StateCompositionContext ctx, in ValueHash256 rootHash) { }

    public void VisitMissingNode(in StateCompositionContext ctx, in ValueHash256 nodeHash)
    {
        Metrics.IncrementScanMissingNodes();
        if (_logger.IsWarn)
            _logger.Warn($"InspectContract: missing node at depth {ctx.Level}");
    }

    public void VisitBranch(in StateCompositionContext ctx, TrieNode node)
    {
        if (!ctx.IsStorage || !_collectingTarget) return;
        int byteSize = node.FullRlp.Length;
        int depth = Math.Min(ctx.Level, VisitorCounters.MaxTrackedDepth - 1);
        _totalNodes++;
        _totalSize += byteSize;
        if (ctx.Level > _maxDepth) _maxDepth = ctx.Level;
        _depths[depth].AddFullNode(byteSize);
    }

    public void VisitExtension(in StateCompositionContext ctx, TrieNode node)
    {
        if (!ctx.IsStorage || !_collectingTarget) return;
        int byteSize = node.FullRlp.Length;
        int depth = Math.Min(ctx.Level, VisitorCounters.MaxTrackedDepth - 1);
        _totalNodes++;
        _totalSize += byteSize;
        if (ctx.Level > _maxDepth) _maxDepth = ctx.Level;
        _depths[depth].AddShortNode(byteSize);
    }

    public void VisitLeaf(in StateCompositionContext ctx, TrieNode node)
    {
        if (!ctx.IsStorage || !_collectingTarget) return;
        int byteSize = node.FullRlp.Length;
        int depth = Math.Min(ctx.Level, VisitorCounters.MaxTrackedDepth - 1);
        _totalNodes++;
        _valueNodes++;
        _totalSize += byteSize;
        if (ctx.Level > _maxDepth) _maxDepth = ctx.Level;
        _depths[depth].AddValueNode(byteSize);
    }

    public void VisitAccount(in StateCompositionContext ctx, TrieNode node, in AccountStruct account)
    {
        if (_collectingTarget)
        {
            _collectingTarget = false;
            _targetCompleted = true;
            return;
        }

        if (!_targetCompleted && account.HasStorage && account.StorageRoot == targetStorageRoot)
            _collectingTarget = true;
    }

    public TopContractEntry? GetResult(ValueHash256 owner, ValueHash256 storageRoot)
    {
        if (!_collectingTarget && !_targetCompleted)
            return null;

        TrieLevelStat[] levels = new TrieLevelStat[VisitorCounters.MaxTrackedDepth];
        TrieLevelStat summary = LevelStatsBuilder.Fill(_depths[..], levels);

        return new TopContractEntry
        {
            Owner = owner,
            StorageRoot = storageRoot,
            MaxDepth = _maxDepth + 1, // +1: Geth counts valueNode as extra depth level
            TotalNodes = _totalNodes + _valueNodes, // Geth: Full+Short(ext+leaf)+Value(leaf)
            ValueNodes = _valueNodes,
            TotalSize = _totalSize,
            Levels = System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(levels),
            Summary = summary,
        };
    }

    public void Dispose() { }
}
