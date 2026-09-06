// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.History.Walk;
using Nethermind.Trie;

namespace Nethermind.State.Flat.History.Proofs;

internal sealed class HistoricalTrieNodeBuilder
{
    private readonly TrieHistoryScope _scope;
    private readonly ulong _block;
    private readonly ResolutionBudget _budget;
    private readonly int _fanOut;
    private readonly ArchiveProofNodeCache? _cache;
    private readonly ParallelOptions _fanOutOptions;
    private ConcurrentDictionary<TreePath, byte[]>? _prefetched;

    public HistoricalTrieNodeBuilder(TrieHistoryScope scope, ulong block, ResolutionBudget budget, int fanOut, ArchiveProofNodeCache? cache)
    {
        _scope = scope;
        _block = block;
        _budget = budget;
        _fanOut = fanOut;
        _cache = cache;
        _fanOutOptions = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, fanOut) };
    }

    public byte[] LoadRlp(in TreePath path, Hash256 expectedHash)
    {
        ValueHash256 expected = expectedHash.ValueHash256;
        if (_cache is not null && _cache.TryGet(expected, out byte[]? cached) && cached is not null) return cached;

        if (_prefetched is not null && _prefetched.TryRemove(path, out byte[]? prefetched) && ValueKeccak.Compute(prefetched) == expected) return Publish(expected, prefetched);

        byte[]? rlp = ResolveRlp(path, _fanOut > 1);
        if (rlp is not null && ValueKeccak.Compute(rlp) == expected) return Publish(expected, rlp);

        rlp = RebuildRlp(path, _fanOut > 1);
        if (rlp is not null && ValueKeccak.Compute(rlp) == expected) return Publish(expected, rlp);

        throw new StateUnavailableException(
            $"The node at {path} as of block {_block} rebuilt to {(rlp is null ? "nothing" : ValueKeccak.Compute(rlp).ToString())} instead of the " +
            $"{expectedHash} its parent commits to. The flat history rows below that path do not reproduce the proven " +
            "state root, so no proof is served for this height.");
    }

    private byte[] Publish(in ValueHash256 hash, byte[] rlp)
    {
        _cache?.Set(hash, rlp);
        return rlp;
    }

    public ParallelOptions FanOutOptions => _fanOutOptions;

    public void CollectPrefetch(in ValueHash256 fullPath, ICollection<(HistoricalTrieNodeBuilder Builder, TreePath Path)> work)
    {
        _prefetched ??= new ConcurrentDictionary<TreePath, byte[]>();
        int depth = 0;
        while (depth < CommitmentDepthPolicy.MaxTrieDepth && (_scope.HasCommitmentRows(depth) || _scope.IsComposed(depth))) depth++;
        for (int length = 0; length < depth; length++)
        {
            TreePath prefix = new(fullPath, CommitmentDepthPolicy.MaxTrieDepth);
            prefix.TruncateMut(length);
            work.Add((this, prefix));
        }
    }

    public static void Prefetch(IReadOnlyList<(HistoricalTrieNodeBuilder Builder, TreePath Path)> work, ParallelOptions options)
    {
        try
        {
            Parallel.ForEach(work, options, static item =>
            {
                byte[]? rlp = item.Builder.ResolveRlp(item.Path, parallelChildren: false, allowRebuild: false);
                if (rlp is not null) item.Builder._prefetched![item.Path] = rlp;
            });
        }
        catch (AggregateException e)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.Flatten().InnerExceptions[0]).Throw();
        }
    }

    private byte[]? ResolveRlp(in TreePath path, bool parallelChildren, bool allowRebuild = true)
    {
        if (_scope.IsComposed(path.Length)) return Compose(path, parallelChildren);
        if (!_scope.HasCommitmentRows(path.Length)) return allowRebuild ? RebuildRlp(path, parallelChildren) : null;

        if (_scope.MayHaveExactRows(path.Length))
        {
            using CommitmentStore.RowChain exact = _scope.OpenRows(path, exact: true, _block, _budget);
            if (exact.MoveNext() && ParentRowCodec.IsValid(exact.CurrentValue))
            {
                if (path.Length == 0) _scope.NoteRootLastBlock(ParentRowCodec.LastBlock(exact.CurrentValue));
                bool sameEpoch = _scope.Policy.Epoch(exact.CurrentSuffix) == _scope.Policy.Epoch(_block);
                if ((sameEpoch || !NewerCheckpointRowExists(path, ParentRowCodec.LastBlock(exact.CurrentValue))) && Materialize(exact) is { } fromExact) return fromExact;
            }
        }

        return ResolveCheckpointed(path, parallelChildren, allowRebuild);
    }

    private bool NewerCheckpointRowExists(in TreePath path, ulong exactLastBlock)
    {
        using CommitmentStore.RowChain chain = _scope.OpenRows(path, exact: false, _scope.Policy.WindowAtOrBelow(_block) + 1, _budget);
        return chain.MoveNext() && ParentRowCodec.IsValid(chain.CurrentValue) && ParentRowCodec.LastBlock(chain.CurrentValue) > exactLastBlock && ParentRowCodec.LastBlock(chain.CurrentValue) <= _block;
    }

    private byte[]? ResolveCheckpointed(in TreePath path, bool parallelChildren, bool allowRebuild)
    {
        ulong anchor = _scope.Policy.WindowAtOrBelow(_block);
        using CommitmentStore.RowChain chain = _scope.OpenRows(path, exact: false, anchor + 1, _budget);
        if (!chain.MoveNext()) return allowRebuild ? RebuildRlp(path, parallelChildren) : null;
        if (!ParentRowCodec.IsValid(chain.CurrentValue)) return RebuildRlp(path, parallelChildren);
        if (path.Length == 0) _scope.NoteRootLastBlock(ParentRowCodec.LastBlock(chain.CurrentValue));
        if (chain.CurrentSuffix <= anchor || ParentRowCodec.LastBlock(chain.CurrentValue) <= _block) return Materialize(chain);

        ReadOnlySpan<byte> movedRow = chain.CurrentValue;
        if (!ParentRowCodec.IsBranchRow(movedRow)) return RebuildRlp(path, parallelChildren);

        ushort presenceMoved = ParentRowCodec.Presence(movedRow);
        ushort changed = ParentRowCodec.Changed(movedRow);
        ChildVector children = ChildVector.Rent();
        try
        {
            ushort presenceAnchor = 0;
            using (CommitmentStore.RowChain anchored = _scope.OpenRows(path, exact: false, anchor, _budget))
            {
                if (anchored.MoveNext() && ParentRowCodec.IsBranchRow(anchored.CurrentValue))
                {
                    presenceAnchor = ParentRowCodec.Presence(anchored.CurrentValue);
                    ushort fromAnchor = (ushort)(presenceMoved & ~changed & presenceAnchor);
                    FillFromChainIncludingCurrent(anchored, fromAnchor, children);
                    if ((fromAnchor & ~children.Presence) != 0) return RebuildRlp(path, parallelChildren);
                }
            }

            ushort recompute = (ushort)(changed | (presenceAnchor & ~presenceMoved) | (presenceMoved & ~presenceAnchor & ~changed));
            ResolveChangedChildren(path, recompute, children, parallelChildren);
            return BranchRlp.Encode(children);
        }
        finally
        {
            ChildVector.Return(children);
        }
    }

    private void ResolveChangedChildren(in TreePath path, ushort changed, ChildVector children, bool parallelChildren)
    {
        if (parallelChildren)
        {
            TreePath parent = path;
            RunFanOut(index =>
            {
                if (((changed >> index) & 1) == 1) ResolveChild(parent.Append(index), children, index);
            });

            return;
        }

        for (int index = 0; index < BranchRlp.ChildCount; index++)
        {
            if (((changed >> index) & 1) == 1) ResolveChild(path.Append(index), children, index);
        }
    }

    private void RunFanOut(Action<int> child)
    {
        try
        {
            Parallel.For(0, BranchRlp.ChildCount, _fanOutOptions, child);
        }
        catch (AggregateException e)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.Flatten().InnerExceptions[0]).Throw();
        }
    }

    private byte[]? Compose(in TreePath path, bool parallelChildren)
    {
        byte[]?[] children = new byte[]?[BranchRlp.ChildCount];
        if (parallelChildren)
        {
            TreePath parent = path;
            RunFanOut(index => children[index] = ResolveRlp(parent.Append(index), parallelChildren: false));
        }
        else
        {
            for (int index = 0; index < BranchRlp.ChildCount; index++) children[index] = ResolveRlp(path.Append(index), parallelChildren: false);
        }

        NodeView[] views = new NodeView[BranchRlp.ChildCount];
        try
        {
            for (int index = 0; index < BranchRlp.ChildCount; index++)
            {
                byte[]? child = children[index];
                views[index] = child is null ? NodeView.Empty : NodeViews.FromRlp(child);
            }

            NodeView composed = NodeViews.Combine(views);
            try
            {
                return composed.Kind == NodeViewKind.Empty ? null : composed.Rlp.ToArray();
            }
            finally
            {
                composed.Release();
            }
        }
        finally
        {
            foreach (NodeView view in views) view.Release();
        }
    }

    private void ResolveChild(in TreePath childPath, ChildVector children, int index)
    {
        byte[]? rlp = ResolveRlp(childPath, parallelChildren: false);
        if (rlp is null)
        {
            children.Clear(index);
            return;
        }

        if (rlp.Length < Hash256.Size)
        {
            children.Set(index, rlp);
            return;
        }

        ValueHash256 hash = ValueKeccak.Compute(rlp);
        children.SetHash(index, hash);
        _cache?.Set(hash, rlp);
    }

    private byte[]? Materialize(CommitmentStore.RowChain chain)
    {
        ReadOnlySpan<byte> newest = chain.CurrentValue;
        if (!ParentRowCodec.IsBranchRow(newest)) return ParentRowCodec.IsWholeNodeRow(newest) ? ParentRowCodec.WholeNodeRlp(newest).ToArray() : null;

        ushort presence = ParentRowCodec.Presence(newest);
        if (presence == 0) return null;

        ChildVector children = ChildVector.Rent();
        try
        {
            ushort missing = (ushort)(presence & ~ParentRowCodec.Fill(newest, presence, children));
            while (missing != 0 && chain.MoveNext())
            {
                ReadOnlySpan<byte> older = chain.CurrentValue;
                if (!ParentRowCodec.IsBranchRow(older)) break;

                missing = (ushort)(missing & ~ParentRowCodec.Fill(older, missing, children));
            }

            return missing == 0 ? BranchRlp.Encode(children) : null;
        }
        finally
        {
            ChildVector.Return(children);
        }
    }

    private static void FillFromChainIncludingCurrent(CommitmentStore.RowChain chain, ushort wanted, ChildVector children)
    {
        ushort missing = (ushort)(wanted & ~ParentRowCodec.Fill(chain.CurrentValue, wanted, children));
        while (missing != 0 && chain.MoveNext())
        {
            ReadOnlySpan<byte> row = chain.CurrentValue;
            if (!ParentRowCodec.IsBranchRow(row)) return;

            missing = (ushort)(missing & ~ParentRowCodec.Fill(row, missing, children));
        }
    }

    private byte[]? RebuildRlp(in TreePath path, bool parallelChildren)
    {
        List<TrieLeaf> leaves = parallelChildren && path.Length < CommitmentDepthPolicy.MaxTrieDepth ? EnumerateLeavesByChild(path) : EnumerateLeaves(path);
        TrieNode? node = SparseTrieBuilder.Build(leaves, path);
        if (node is null) return null;

        PublishSubtree(node);
        return node.FullRlp.ToArray();
    }

    private List<TrieLeaf> EnumerateLeaves(in TreePath path)
    {
        List<TrieLeaf> leaves = [];
        _scope.EnumerateLeaves(path, _block, _budget, leaves);
        return leaves;
    }

    private List<TrieLeaf> EnumerateLeavesByChild(in TreePath path)
    {
        List<TrieLeaf>[] parts = new List<TrieLeaf>[BranchRlp.ChildCount];
        TreePath parent = path;
        RunFanOut(index => parts[index] = EnumerateLeaves(parent.Append(index)));
        int total = 0;
        foreach (List<TrieLeaf> part in parts) total += part.Count;
        List<TrieLeaf> leaves = new(total);
        foreach (List<TrieLeaf> part in parts) leaves.AddRange(part);
        return leaves;
    }

    private void PublishSubtree(TrieNode node)
    {
        if (_cache is null) return;

        int children = node.IsBranch ? BranchRlp.ChildCount : node.IsExtension ? 1 : 0;
        for (int index = 0; index < children; index++)
        {
            if (!node.TryGetDirtyChild(index, out TrieNode? child)) continue;

            if (child.Keccak is { } keccak && child.FullRlp.ToArray() is { } rlp) _cache.Set(keccak.ValueHash256, rlp);
            PublishSubtree(child);
        }
    }
}
