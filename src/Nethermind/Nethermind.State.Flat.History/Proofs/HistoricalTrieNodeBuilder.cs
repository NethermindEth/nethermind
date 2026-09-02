// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.State.Flat.History.Proofs;

internal sealed class HistoricalTrieNodeBuilder(
    TrieHistoryScope scope,
    ValueHash256 trieScope,
    ulong block,
    ResolutionBudget budget,
    int fanOut,
    ArchiveProofNodeCache? cache)
{
    private readonly ParallelOptions _fanOutOptions = new() { MaxDegreeOfParallelism = Math.Max(1, fanOut) };

    public byte[] LoadRlp(in TreePath path, Hash256 expectedHash)
    {
        if (cache is not null && cache.TryGet(trieScope, path, block, out byte[]? cached) && cached is not null) return cached;

        byte[]? rlp = ResolveRlp(path, fanOut > 1);
        if (rlp is not null && Keccak.Compute(rlp) == expectedHash) return Publish(path, rlp);

        rlp = RebuildRlp(path);
        if (rlp is not null && Keccak.Compute(rlp) == expectedHash) return Publish(path, rlp);

        throw new StateUnavailableException(
            $"The node at {path} as of block {block} rebuilt to {(rlp is null ? "nothing" : Keccak.Compute(rlp).ToString())} instead of the " +
            $"{expectedHash} its parent commits to. The flat history rows below that path do not reproduce the proven " +
            "state root, so no proof is served for this height.");
    }

    private byte[] Publish(in TreePath path, byte[] rlp)
    {
        cache?.Set(trieScope, path, block, rlp);
        return rlp;
    }

    private byte[]? ResolveRlp(in TreePath path, bool parallelChildren)
    {
        CommitmentTier tier = scope.TierOf(path.Length);
        if (scope.MayHaveExactRows(path.Length))
        {
            using CommitmentStore.RowChain exact = scope.OpenRows(path, exact: true, block);
            if (exact.MoveNext() && ParentRowCodec.IsValid(exact.CurrentValue) && !NewerCheckpointRowExists(path, ParentRowCodec.LastBlock(exact.CurrentValue)) && Materialize(exact) is { } fromExact)
            {
                return fromExact;
            }
        }

        return tier switch
        {
            CommitmentTier.PerChange or CommitmentTier.Checkpoint => ResolveCheckpointed(path, parallelChildren),
            _ => RebuildRlp(path),
        };
    }

    private bool NewerCheckpointRowExists(in TreePath path, ulong exactLastBlock)
    {
        using CommitmentStore.RowChain chain = scope.OpenRows(path, exact: false, scope.Policy.WindowAtOrBelow(block) + 1);
        return chain.MoveNext() && ParentRowCodec.IsValid(chain.CurrentValue) && ParentRowCodec.LastBlock(chain.CurrentValue) > exactLastBlock && ParentRowCodec.LastBlock(chain.CurrentValue) <= block;
    }

    private byte[]? ResolveCheckpointed(in TreePath path, bool parallelChildren)
    {
        ulong anchor = scope.Policy.WindowAtOrBelow(block);
        using CommitmentStore.RowChain chain = scope.OpenRows(path, exact: false, anchor + 1);
        if (!chain.MoveNext()) return RebuildRlp(path);
        if (!ParentRowCodec.IsValid(chain.CurrentValue)) return RebuildRlp(path);
        if (chain.CurrentSuffix <= anchor || ParentRowCodec.LastBlock(chain.CurrentValue) <= block) return Materialize(chain);

        ReadOnlySpan<byte> movedRow = chain.CurrentValue;
        if (!ParentRowCodec.IsBranchRow(movedRow)) return RebuildRlp(path);

        ushort presenceMoved = ParentRowCodec.Presence(movedRow);
        ushort changed = (ushort)(ParentRowCodec.Changed(movedRow) & presenceMoved);
        byte[]?[] children = new byte[]?[BranchRlp.ChildCount];

        ushort presenceAnchor = 0;
        using (CommitmentStore.RowChain anchored = scope.OpenRows(path, exact: false, anchor))
        {
            if (anchored.MoveNext() && ParentRowCodec.IsBranchRow(anchored.CurrentValue))
            {
                presenceAnchor = ParentRowCodec.Presence(anchored.CurrentValue);
                ushort fromAnchor = (ushort)(presenceMoved & ~changed & presenceAnchor);
                FillFromChainIncludingCurrent(anchored, fromAnchor, children);
                if (Missing(fromAnchor, children) != 0) return RebuildRlp(path);
            }
        }

        ushort recompute = (ushort)(changed | (presenceAnchor & ~presenceMoved) | (presenceMoved & ~presenceAnchor & ~changed));
        ResolveChangedChildren(path, recompute, children, parallelChildren);
        return BranchRlp.Encode(children);
    }

    private void ResolveChangedChildren(in TreePath path, ushort changed, byte[]?[] children, bool parallelChildren)
    {
        TreePath parent = path;
        if (parallelChildren)
        {
            try
            {
                Parallel.For(0, BranchRlp.ChildCount, _fanOutOptions, index =>
                {
                    if (((changed >> index) & 1) == 1) children[index] = ResolveReference(parent.Append(index));
                });
            }
            catch (AggregateException e) when (e.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            }

            return;
        }

        for (int index = 0; index < BranchRlp.ChildCount; index++)
        {
            if (((changed >> index) & 1) == 1) children[index] = ResolveReference(parent.Append(index));
        }
    }

    private byte[]? ResolveReference(in TreePath childPath)
    {
        byte[]? rlp = ResolveRlp(childPath, parallelChildren: false);
        return rlp is null ? null : BranchRlp.ReferenceOf(rlp);
    }

    private byte[]? Materialize(CommitmentStore.RowChain chain)
    {
        ReadOnlySpan<byte> newest = chain.CurrentValue;
        if (!ParentRowCodec.IsBranchRow(newest)) return ParentRowCodec.IsWholeNodeRow(newest) ? ParentRowCodec.WholeNodeRlp(newest).ToArray() : null;

        ushort presence = ParentRowCodec.Presence(newest);
        if (presence == 0) return null;

        byte[]?[] children = new byte[]?[BranchRlp.ChildCount];
        ushort missing = (ushort)(presence & ~ParentRowCodec.Fill(newest, presence, children));
        while (missing != 0 && chain.MoveNext())
        {
            ReadOnlySpan<byte> older = chain.CurrentValue;
            if (!ParentRowCodec.IsBranchRow(older)) break;

            missing = (ushort)(missing & ~ParentRowCodec.Fill(older, missing, children));
        }

        return missing == 0 ? BranchRlp.Encode(children) : null;
    }

    private static void FillFromChainIncludingCurrent(CommitmentStore.RowChain chain, ushort wanted, byte[]?[] children)
    {
        ushort missing = (ushort)(wanted & ~ParentRowCodec.Fill(chain.CurrentValue, wanted, children));
        while (missing != 0 && chain.MoveNext())
        {
            ReadOnlySpan<byte> row = chain.CurrentValue;
            if (!ParentRowCodec.IsBranchRow(row)) return;

            missing = (ushort)(missing & ~ParentRowCodec.Fill(row, missing, children));
        }
    }

    private static ushort Missing(ushort wanted, byte[]?[] children)
    {
        ushort missing = 0;
        for (int index = 0; index < BranchRlp.ChildCount; index++)
        {
            if (((wanted >> index) & 1) == 1 && children[index] is null) missing |= (ushort)(1 << index);
        }

        return missing;
    }

    private byte[]? RebuildRlp(in TreePath path)
    {
        List<TrieLeaf> leaves = [];
        scope.EnumerateLeaves(path, block, budget, leaves);
        TrieNode? node = SparseTrieBuilder.Build(leaves, path);
        return node?.FullRlp.ToArray();
    }
}
