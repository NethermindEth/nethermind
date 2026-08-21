// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Db.LogIndex;

namespace Nethermind.History;

/// <summary>Retains the receipts of blocks that touched a per-contract history slice, so those addresses stay
/// queryable after the general history pruner has deleted the block. The bloom is only a first filter; where the
/// log index covers the block it confirms the hit, since a bloom match on a busy contract is near-certain.</summary>
public sealed class SlicedReceiptRetention(IFlatDbConfig flatDbConfig, ILogIndexStorage logIndexStorage) : IPrunedReceiptRetention
{
    private readonly FrozenSet<Address> _addresses = ParseAddresses(flatDbConfig.HistorySliceAddresses);

    public bool ShouldRetainReceipts(Block block)
    {
        if (_addresses.Count == 0)
        {
            return false;
        }

        Bloom? bloom = block.Header.Bloom;
        if (bloom is null)
        {
            return false;
        }

        // The index is int-keyed, so a block beyond int.MaxValue cannot be asked about; the bloom match alone
        // decides there rather than a wrapped negative range silently reporting no hit.
        bool indexCoversBlock = logIndexStorage.Enabled
            && block.Number <= int.MaxValue
            && logIndexStorage.MinBlockNumber is { } min && block.Number >= (ulong)min
            && logIndexStorage.MaxBlockNumber is { } max && block.Number <= (ulong)max;

        foreach (Address address in _addresses)
        {
            if (!bloom.Matches(address))
            {
                continue;
            }

            if (!indexCoversBlock)
            {
                return true;
            }

            int blockNumber = (int)block.Number;
            using IEnumerator<int> hits = logIndexStorage.GetEnumerator(address, blockNumber, blockNumber);
            if (hits.MoveNext())
            {
                return true;
            }
        }

        return false;
    }

    private static FrozenSet<Address> ParseAddresses(string? raw) =>
        SliceScopeConfig.Parse(raw).Select(static entry => entry.Address).ToFrozenSet();
}
