// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie;

public class TransitionQueryVisitor : ITreeVisitor<TreePathContextWithStorage>, IDisposable
{
    private bool _skipAccountStartHashComparison = false;
    private bool _skipStorageStartHashComparison = false;
    private readonly TreePath _startHash;
    private readonly TreePath _storageStartHash;

    public TreePath CurrentAccountPath;
    public TreePath CurrentStoragePath;

    private int _currentLeafCount;

    private readonly IValueCollector _valueCollector;

    private readonly int _nodeLimit;

    public bool StoppedEarly { get; set; } = false;
    public bool IsFullDbScan => false;
    public bool IsRangeScan => true;
    private readonly CancellationToken _cancellationToken;

    public ReadFlags ExtraReadFlag { get; }

    public TransitionQueryVisitor(
        in ValueHash256 accountsStartHash,
        in ValueHash256 storageStartHash,
        IValueCollector valueCollector,
        int nodeLimit = 10000,
        ReadFlags readFlags = ReadFlags.None,
        CancellationToken cancellationToken = default)
    {
        if (accountsStartHash == ValueKeccak.Zero)
            _skipAccountStartHashComparison = true;
        else
            _startHash = new TreePath(accountsStartHash, 64);

        if (storageStartHash == ValueKeccak.Zero)
            _skipStorageStartHashComparison = true;
        else
            _storageStartHash = new TreePath(storageStartHash, 64);

        _cancellationToken = cancellationToken;
        _valueCollector = valueCollector;
        _nodeLimit = nodeLimit;
        ExtraReadFlag = readFlags;
    }

    private bool ShouldVisit(in TreePath path, bool isStorage)
    {
        if (_cancellationToken.IsCancellationRequested)
        {
            StoppedEarly = true;
            return false;
        }

        if (_currentLeafCount >= _nodeLimit)
        {
            StoppedEarly = true;
            return false;
        }

        if (isStorage)
        {
            if (_skipStorageStartHashComparison) return true;
            if (_storageStartHash.CompareToTruncated(path, path.Length) > 0)
            {
                return false;
            }
        }
        else
        {
            if (_skipAccountStartHashComparison) return true;
            if (_startHash.CompareToTruncated(path, path.Length) > 0)
            {
                return false;
            }
        }
        return true;
    }

    public bool ShouldVisit(in TreePathContextWithStorage ctx, Hash256 nextNode)
    {
        return ShouldVisit(ctx.Path, ctx.Storage is not null);
    }

    public void VisitTree(in TreePathContextWithStorage nodeContext, Hash256 rootHash, TrieVisitContext trieVisitContext)
    {
    }


    public void VisitMissingNode(in TreePathContextWithStorage ctx, Hash256 nodeHash, TrieVisitContext trieVisitContext)
    {
        throw new TrieException($"Missing node {ctx.Path} {nodeHash}");
    }

    public void VisitBranch(in TreePathContextWithStorage ctx, TrieNode node, TrieVisitContext trieVisitContext)
    {
    }

    public void VisitExtension(in TreePathContextWithStorage ctx, TrieNode node, TrieVisitContext trieVisitContext)
    {
    }

    public void VisitLeaf(in TreePathContextWithStorage ctx, TrieNode node, TrieVisitContext trieVisitContext, ReadOnlySpan<byte> value)
    {
        TreePath path = ctx.Path.Append(node.Key);
        if (!ShouldVisit(path, trieVisitContext.IsStorage))
        {
            return;
        }

        switch (trieVisitContext.IsStorage)
        {
            case true:
                _valueCollector.CollectStorage(ctx.Storage, in path.Path, node.Value);
                // We found at least one leaf, don't compare with startHash anymore
                _skipStorageStartHashComparison = true;
                CurrentAccountPath = new TreePath(ctx.Storage, 64);
                CurrentStoragePath = new TreePath(path.Path, 64);
                break;
            case false:
                _valueCollector.CollectAccount(in path.Path, node.Value);
                // We found at least one leaf, don't compare with startHash anymore
                _skipAccountStartHashComparison = true;
                CurrentAccountPath = new TreePath(path.Path, 64);
                CurrentStoragePath = TreePath.Empty;
                break;
        }
        _currentLeafCount++;
    }

    public void VisitCode(in TreePathContextWithStorage nodeContext, Hash256 codeHash, TrieVisitContext trieVisitContext)
    {
    }

    public void Dispose()
    {
    }

    public interface IValueCollector
    {
        void CollectAccount(in ValueHash256 path, CappedArray<byte> value);
        // no needed because when we move the account we already have a codeHash and we can query the _codeDb directly
        // int CollectCode(in ValueHash256 account, CappedArray<byte> value);
        void CollectStorage(in ValueHash256 account, in ValueHash256 path, CappedArray<byte> value);
    }

}
