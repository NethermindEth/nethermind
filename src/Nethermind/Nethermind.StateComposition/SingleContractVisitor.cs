// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Immutable;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.StateComposition;

/// <summary>
/// Specialized visitor that walks the full state trie but only collects storage statistics
/// for a single target contract identified by its storage root.
/// Skips all non-target storage tries for efficiency.
/// </summary>
internal sealed class SingleContractVisitor : ITreeVisitor<StateCompositionContext>, IDisposable
{
    private readonly ILogger _logger;
    private readonly CancellationToken _ct;
    private readonly ValueHash256 _targetStorageRoot;

    private bool _collectingTarget;
    private bool _targetCompleted;

    private int _maxDepth;
    private long _totalNodes;
    private long _valueNodes;
    private long _totalSize;
    private readonly DepthCounter[] _depths = new DepthCounter[VisitorCounters.MaxTrackedDepth];

    public bool IsFullDbScan => true;
    public ReadFlags ExtraReadFlag => ReadFlags.HintCacheMiss;
    public bool ExpectAccounts => true;

    public SingleContractVisitor(ILogManager logManager, CancellationToken ct, ValueHash256 targetStorageRoot)
    {
        _logger = logManager.GetClassLogger();
        _ct = ct;
        _targetStorageRoot = targetStorageRoot;
    }

    public bool ShouldVisit(in StateCompositionContext ctx, in ValueHash256 nextNode)
    {
        if (_ct.IsCancellationRequested) return false;
        if (_targetCompleted) return false;
        if (ctx.IsStorage && !_collectingTarget) return false;
        return true;
    }

    public void VisitTree(in StateCompositionContext ctx, in ValueHash256 rootHash) { }

    public void VisitMissingNode(in StateCompositionContext ctx, in ValueHash256 nodeHash)
    {
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

        if (!_targetCompleted && account.HasStorage && account.StorageRoot == _targetStorageRoot)
        {
            _collectingTarget = true;
        }
    }

    public TopContractEntry? GetResult(ValueHash256 owner, ValueHash256 storageRoot)
    {
        if (!_collectingTarget && !_targetCompleted)
            return null;

        ImmutableArray<TrieLevelStat>.Builder levelsBuilder =
            ImmutableArray.CreateBuilder<TrieLevelStat>(VisitorCounters.MaxTrackedDepth);
        long summaryShort = 0, summaryFull = 0, summaryValue = 0, summarySize = 0;

        for (int i = 0; i < VisitorCounters.MaxTrackedDepth; i++)
        {
            ref DepthCounter dc = ref _depths[i];
            levelsBuilder.Add(new TrieLevelStat
            {
                Depth = i,
                ShortNodeCount = dc.ShortNodes,
                FullNodeCount = dc.FullNodes,
                ValueNodeCount = dc.ValueNodes,
                TotalSize = dc.TotalSize,
            });
            summaryShort += dc.ShortNodes;
            summaryFull += dc.FullNodes;
            summaryValue += dc.ValueNodes;
            summarySize += dc.TotalSize;
        }

        return new TopContractEntry
        {
            Owner = owner,
            StorageRoot = storageRoot,
            MaxDepth = _maxDepth,
            TotalNodes = _totalNodes,
            ValueNodes = _valueNodes,
            TotalSize = _totalSize,
            Levels = levelsBuilder.MoveToImmutable(),
            Summary = new TrieLevelStat
            {
                Depth = -1,
                ShortNodeCount = summaryShort,
                FullNodeCount = summaryFull,
                ValueNodeCount = summaryValue,
                TotalSize = summarySize,
            },
        };
    }

    public void Dispose() { }
}
