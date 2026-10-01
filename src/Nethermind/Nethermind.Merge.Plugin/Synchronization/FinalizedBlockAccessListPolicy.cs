// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.History;
using Nethermind.Synchronization;

namespace Nethermind.Merge.Plugin.Synchronization;

/// <summary>Selects BAL catch-up blocks on the consensus-finalized ancestry and preserves receipt retention.</summary>
public sealed class FinalizedBlockAccessListPolicy(
    ISyncConfig syncConfig,
    IBeaconSyncStrategy beaconSync,
    IBlockTree blockTree,
    ISpecProvider specProvider,
    IReceiptConfig receiptConfig,
    Func<IHistoryPruner> historyPruner,
    IPrunedReceiptRetention receiptRetention)
{
    private readonly object _lock = new();
    private readonly Dictionary<ulong, Hash256> _ancestors = [];
    private Hash256? _checkpoint;
    private BlockHeader? _oldestAncestor;

    /// <summary>Whether this header belongs to the finalized ancestry and supports reconstruction.</summary>
    public bool CanReconstruct(BlockHeader header)
    {
        if (!syncConfig.ReconstructFinalizedStateFromBlockAccessLists || header.IsGenesis || !header.Difficulty.IsZero
            || header.BlockAccessListHash is null || !beaconSync.MergeTransitionFinished)
            return false;

        IReleaseSpec spec = specProvider.GetSpec(header);
        if (!spec.IsEip7928Enabled || !spec.IsEip6780Enabled) return false;

        lock (_lock)
        {
            Hash256? finalized = beaconSync.GetFinalizedHash();
            if (finalized is null || finalized == Keccak.Zero) return false;
            if (_checkpoint != finalized || _oldestAncestor is null)
            {
                _ancestors.Clear();
                _checkpoint = finalized;
                _oldestAncestor = blockTree.FindHeader(finalized, BlockTreeLookupOptions.None);
                if (_oldestAncestor is not null) _ancestors[_oldestAncestor.Number] = finalized;
            }

            while (_oldestAncestor is { } ancestor && ancestor.Number > header.Number)
            {
                BlockHeader? parent = blockTree.FindHeader(ancestor.ParentHash!, BlockTreeLookupOptions.None);
                if (parent is null || parent.Number != ancestor.Number - 1) return false;
                _ancestors[parent.Number] = ancestor.ParentHash!;
                _oldestAncestor = parent;
            }

            return header.Hash is { } hash && _ancestors.TryGetValue(header.Number, out Hash256? expected) && hash == expected;
        }
    }

    /// <summary>Whether receipts must be available before this block can bypass execution.</summary>
    public bool NeedsReceipts(BlockHeader header) => receiptConfig.StoreReceipts
        && (historyPruner().CutoffBlockNumber is not { } cutoff || header.Number >= cutoff
            || receiptRetention.ShouldRetainReceipts(header));
}
