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
    private BlockHeader? _finalizedHeader;

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
            if ((_checkpoint != finalized || _oldestAncestor is null) && !UpdateCheckpoint(finalized)) return false;

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

    private bool UpdateCheckpoint(Hash256 finalized)
    {
        BlockHeader? latest = blockTree.FindHeader(finalized, BlockTreeLookupOptions.None);
        if (latest is null) return false;

        Dictionary<ulong, Hash256> extension = [];
        BlockHeader cursor = latest;
        if (_finalizedHeader is not null)
        {
            while (cursor.Number > _finalizedHeader.Number)
            {
                extension[cursor.Number] = cursor.Hash!;
                BlockHeader? parent = blockTree.FindHeader(cursor.ParentHash!, BlockTreeLookupOptions.None);
                if (parent is null || parent.Number != cursor.Number - 1) return false;
                cursor = parent;
            }
        }

        if (_finalizedHeader is null || cursor.Hash != _checkpoint)
        {
            _ancestors.Clear();
            _oldestAncestor = latest;
        }
        else
        {
            foreach ((ulong number, Hash256 hash) in extension) _ancestors[number] = hash;
        }

        _checkpoint = finalized;
        _finalizedHeader = latest;
        _ancestors[latest.Number] = finalized;
        return true;
    }

    /// <summary>Whether receipts must be available before this block can bypass execution.</summary>
    public bool NeedsReceipts(BlockHeader header) => receiptConfig.StoreReceipts
        && (historyPruner().CutoffBlockNumber is not { } cutoff || header.Number >= cutoff
            || receiptRetention.ShouldRetainReceipts(header));
}
