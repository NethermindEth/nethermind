// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using System.Collections.Generic;
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

    /// <summary>
    /// Asks the log index once per address for the whole span, instead of once per block. Only the part of the span
    /// the index actually covers is answered: outside it a bloom match is the deciding test, and testing that needs
    /// the header, which is the per-block read the caller is trying to avoid.
    /// </summary>
    public IReadOnlySet<ulong> RetainedHeights(ulong fromInclusive, ulong toExclusive, out ulong answeredFrom, out ulong answeredTo)
    {
        if (_addresses.Count == 0)
        {
            // Nothing is retained at any height, so the whole span is answered - and answered with nothing.
            answeredFrom = fromInclusive;
            answeredTo = toExclusive;
            return FrozenSet<ulong>.Empty;
        }

        answeredFrom = fromInclusive;
        answeredTo = fromInclusive;

        if (!logIndexStorage.Enabled
            || logIndexStorage.MinBlockNumber is not { } min
            || logIndexStorage.MaxBlockNumber is not { } max)
        {
            return FrozenSet<ulong>.Empty;
        }

        // The index is int-keyed, so a span beyond int.MaxValue cannot be asked about rather than wrapping negative.
        ulong coveredFrom = ulong.Max(fromInclusive, (ulong)min);
        ulong coveredTo = ulong.Min(toExclusive, ulong.Min((ulong)max + 1, int.MaxValue));
        if (coveredFrom >= coveredTo)
        {
            return FrozenSet<ulong>.Empty;
        }

        HashSet<ulong> retained = [];
        foreach (Address address in _addresses)
        {
            using IEnumerator<int> hits = logIndexStorage.GetEnumerator(address, (int)coveredFrom, (int)(coveredTo - 1));
            while (hits.MoveNext())
            {
                retained.Add((ulong)hits.Current);
            }
        }

        answeredFrom = coveredFrom;
        answeredTo = coveredTo;
        return retained;
    }

    private static FrozenSet<Address> ParseAddresses(string? raw)
    {
        HashSet<Address> addresses = [];
        foreach (SliceScopeEntry entry in SliceScopeConfig.Parse(raw))
        {
            addresses.Add(entry.Address);
        }

        return addresses.ToFrozenSet();
    }
}
