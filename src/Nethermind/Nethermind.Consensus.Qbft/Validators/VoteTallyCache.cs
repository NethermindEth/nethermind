// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Config;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Qbft.Validators;

/// <summary>Applies a block's vote to a tally: epoch blocks reset the outstanding votes instead.</summary>
public sealed class VoteTallyUpdater(EpochManager epochManager, BftBlockInterface blockInterface)
{
    public void UpdateForBlock(BlockHeader header, VoteTally tally)
    {
        if (epochManager.IsEpochBlock((long)header.Number))
        {
            tally.DiscardOutstandingVotes();
            return;
        }

        ValidatorVote? vote = blockInterface.ExtractVote(header);
        if (vote is not null)
        {
            tally.AddVote(vote);
        }
    }
}

/// <summary>
/// Tallies validator votes along the chain and caches the tally after each block; walks back to the
/// nearest epoch block (or cached ancestor) when a block has not been tallied yet.
/// </summary>
public class VoteTallyCache(IBlockTree blockTree, VoteTallyUpdater updater, EpochManager epochManager, BftBlockInterface blockInterface)
{
    private const int CacheSize = 100;
    private readonly LruCache<Hash256AsKey, VoteTally> _cache = new(CacheSize, nameof(VoteTallyCache));
    private readonly Lock _lock = new();

    protected EpochManager EpochManager { get; } = epochManager;

    public VoteTally GetVoteTallyAtHead() => GetVoteTallyAfterBlock(blockTree.Head?.Header ?? throw new InvalidOperationException("Block tree has no head."));

    public VoteTally GetVoteTallyAfterBlock(BlockHeader header)
    {
        lock (_lock)
        {
            Hash256 hash = header.Hash ?? throw new ArgumentException("Header has no hash.", nameof(header));
            return _cache.TryGet(hash, out VoteTally? tally) ? tally : PopulateCacheUpToAndIncluding(header);
        }
    }

    private VoteTally PopulateCacheUpToAndIncluding(BlockHeader start)
    {
        Stack<BlockHeader> intermediate = new();
        BlockHeader header = start;
        VoteTally? tally;
        while (true)
        {
            intermediate.Push(header);
            tally = GetValidatorsAfter(header);
            if (tally is not null)
            {
                break;
            }

            header = blockTree.FindHeader(header.ParentHash!, BlockTreeLookupOptions.None)
                     ?? throw new InvalidOperationException("Supplied block was on an orphaned chain, unable to generate VoteTally.");
        }

        VoteTally mutable = tally.Copy();
        while (intermediate.Count > 0)
        {
            BlockHeader h = intermediate.Pop();
            updater.UpdateForBlock(h, mutable);
            _cache.Set(h.Hash!, mutable.Copy());
        }

        return mutable;
    }

    /// <summary>The tally after <paramref name="header"/> when it can be derived without walking further back.</summary>
    protected virtual VoteTally? GetValidatorsAfter(BlockHeader header)
    {
        if (EpochManager.IsEpochBlock((long)header.Number))
        {
            return new VoteTally(blockInterface.ValidatorsInBlock(header));
        }

        return _cache.TryGet(header.ParentHash!, out VoteTally? parentTally) ? parentTally : null;
    }
}

/// <summary>A <see cref="VoteTallyCache"/> that honours the validator lists installed by <c>transitions.qbft</c>.</summary>
public sealed class ForkingVoteTallyCache(
    IBlockTree blockTree,
    VoteTallyUpdater updater,
    EpochManager epochManager,
    BftBlockInterface blockInterface,
    BftForksSchedule forksSchedule) : VoteTallyCache(blockTree, updater, epochManager, blockInterface)
{
    protected override VoteTally? GetValidatorsAfter(BlockHeader header)
    {
        IReadOnlyList<Address>? overridden = forksSchedule.GetValidatorOverride(header.Number + 1);
        return overridden is not null ? new VoteTally(overridden) : base.GetValidatorsAfter(header);
    }
}
