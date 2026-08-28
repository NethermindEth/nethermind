// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Db.LogIndex;

namespace Nethermind.History;

/// <summary>Retains the receipts and body of blocks that touched a per-contract history slice, so those addresses
/// stay queryable where the general history pruner reclaims. A bounded slice retains only heights inside its own
/// window, measured from the head at sweep time. The bloom is only a first filter; where the
/// log index covers the block it confirms the hit, since a bloom match on a busy contract is near-certain.</summary>
public sealed class SlicedReceiptRetention(IFlatDbConfig flatDbConfig, ILogIndexStorage logIndexStorage, IBlockTree blockTree) : IPrunedReceiptRetention, IPrunedLogsRetention
{
    private readonly FrozenDictionary<Address, ulong?> _slices = ParseSlices(flatDbConfig.HistorySliceAddresses);

    public bool ShouldRetainReceipts(BlockHeader header)
    {
        if (_slices.Count == 0)
        {
            return false;
        }

        ulong head = HeadNumber;

        Bloom? bloom = header.Bloom;
        if (bloom is null)
        {
            return false;
        }

        // The index is int-keyed, so a block beyond int.MaxValue cannot be asked about; the bloom match alone
        // decides there rather than a wrapped negative range silently reporting no hit.
        bool indexCoversBlock = logIndexStorage.Enabled
            && header.Number <= int.MaxValue
            && logIndexStorage.MinBlockNumber is { } min && header.Number >= (ulong)min
            && logIndexStorage.MaxBlockNumber is { } max && header.Number <= (ulong)max;

        foreach ((Address address, ulong? retention) in _slices)
        {
            if (!InsideSliceWindow(header.Number, retention, head) || !bloom.Matches(address))
            {
                continue;
            }

            if (!indexCoversBlock)
            {
                return true;
            }

            int blockNumber = (int)header.Number;
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
        if (_slices.Count == 0)
        {
            // Nothing is retained at any height, so the whole span is answered - and answered with nothing.
            answeredFrom = fromInclusive;
            answeredTo = toExclusive;
            return FrozenSet<ulong>.Empty;
        }

        ulong head = HeadNumber;

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
        foreach ((Address address, ulong? retention) in _slices)
        {
            ulong sliceFrom = coveredFrom;
            if (retention is { } bound)
            {
                ulong sliceFloor = head > bound ? head - bound : 0;
                if (sliceFloor >= coveredTo) continue;
                sliceFrom = ulong.Max(coveredFrom, sliceFloor);
            }

            using IEnumerator<int> hits = logIndexStorage.GetEnumerator(address, (int)sliceFrom, (int)(coveredTo - 1));
            while (hits.MoveNext())
            {
                retained.Add((ulong)hits.Current);
            }
        }

        answeredFrom = coveredFrom;
        answeredTo = coveredTo;
        return retained;
    }

    /// <inheritdoc/>
    public bool RetainsLogsFor(IReadOnlyCollection<AddressAsKey> addresses, ulong fromBlock, ulong toBlock)
    {
        if (addresses.Count == 0 || _slices.Count == 0)
        {
            return false;
        }

        ulong head = HeadNumber;
        foreach (AddressAsKey address in addresses)
        {
            if (!_slices.TryGetValue(address.Value, out ulong? retention) || !InsideSliceWindow(fromBlock, retention, head))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public ulong ExpiredRetentionUpperBound()
    {
        ulong upperBound = 0;
        ulong head = HeadNumber;
        foreach ((_, ulong? retention) in _slices)
        {
            if (retention is not { } bound || head <= bound) continue;

            ulong sliceFloor = head - bound;
            if (upperBound == 0 || sliceFloor < upperBound) upperBound = sliceFloor;
        }

        return upperBound;
    }

    private ulong HeadNumber => blockTree.Head?.Number ?? 0;

    private static bool InsideSliceWindow(ulong height, ulong? retention, ulong head) =>
        retention is not { } bound || head <= bound || height >= head - bound;

    /// <summary>An address named twice keeps its deepest bound; unbounded wins outright.</summary>
    private static FrozenDictionary<Address, ulong?> ParseSlices(string? raw)
    {
        Dictionary<Address, ulong?> slices = [];
        foreach (SliceScopeEntry entry in SliceScopeConfig.Parse(raw))
        {
            if (slices.TryGetValue(entry.Address, out ulong? existing)
                && (existing is null || (entry.RetentionBlocks is { } incoming && incoming <= existing)))
            {
                continue;
            }

            slices[entry.Address] = entry.RetentionBlocks;
        }

        return slices.ToFrozenDictionary();
    }
}
