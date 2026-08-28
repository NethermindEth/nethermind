// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Db.LogIndex;
using Nethermind.Logging;

namespace Nethermind.History;

/// <summary>Retains the receipts and body of blocks that touched a per-contract history slice, so those addresses
/// stay queryable where the general history pruner reclaims. A bounded slice retains only heights inside its own
/// window, measured from the head at sweep time. The bloom is only a first filter; where the
/// log index covers the block it confirms the hit, since a bloom match on a busy contract is near-certain.</summary>
public sealed class SlicedReceiptRetention(IFlatDbConfig flatDbConfig, ILogIndexStorage logIndexStorage, IBlockTree blockTree, IDbProvider? dbProvider = null, ILogManager? logManager = null) : IPrunedReceiptRetention, IPrunedLogsRetention
{
    private const int StampValueLength = 2 * sizeof(ulong);

    private static ReadOnlySpan<byte> StampKeyPrefix => "history:sliceLogsFrom:"u8;

    private readonly FrozenDictionary<Address, ulong?> _slices = ParseSlices(flatDbConfig.HistorySliceAddresses);
    private readonly ConcurrentDictionary<AddressAsKey, ulong> _stampCache = new();
    private readonly ILogger _logger = (logManager ?? LimboLogs.Instance).GetClassLogger<SlicedReceiptRetention>();

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

        if (blockTree.Head?.Number is not { } head)
        {
            return false;
        }

        foreach (AddressAsKey address in addresses)
        {
            if (!_slices.TryGetValue(address.Value, out ulong? retention)
                || !InsideSliceWindow(fromBlock, retention, head)
                || fromBlock < StampedRetainedFrom(address))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Records, per configured address, the height its receipt retention has provably been in force
    /// from. An address first seen here is stamped at <paramref name="oldestStoredReceipts"/> - anything below
    /// was reclaimed before this retention existed, or never stored at all. An address whose record trails
    /// <paramref name="reclaimedThrough"/> missed retention-aware reclaims while unconfigured, so its earned depth
    /// lapsed and it restarts. The stamp never lowers itself: receipts backfilled below it stay refused rather
    /// than guessed at.</summary>
    public void OnPruningPassStarting(ulong oldestStoredReceipts, ulong reclaimedThrough)
    {
        if (_slices.Count == 0 || dbProvider?.MetadataDb is not { } metadata) return;

        Span<byte> key = stackalloc byte[StampKeyPrefix.Length + Address.Size];
        StampKeyPrefix.CopyTo(key);
        Span<byte> value = stackalloc byte[StampValueLength];

        foreach (Address address in _slices.Keys)
        {
            address.Bytes.CopyTo(key[StampKeyPrefix.Length..]);
            byte[]? stored = metadata.Get(key);
            ulong stampFrom;
            if (stored is { Length: StampValueLength })
            {
                stampFrom = BinaryPrimitives.ReadUInt64BigEndian(stored);
                ulong storedThrough = BinaryPrimitives.ReadUInt64BigEndian(stored.AsSpan(sizeof(ulong)));
                if (reclaimedThrough > storedThrough)
                {
                    stampFrom = oldestStoredReceipts;
                    if (_logger.IsInfo) _logger.Info(
                        $"Slice log coverage for {address} restarts at #{stampFrom}: heights up to there were reclaimed while it was not configured.");
                }
                else
                {
                    _stampCache[address] = stampFrom;
                    continue;
                }
            }
            else
            {
                stampFrom = oldestStoredReceipts;
                if (_logger.IsInfo) _logger.Info($"Slice log coverage for {address} starts at #{stampFrom}.");
            }

            BinaryPrimitives.WriteUInt64BigEndian(value, stampFrom);
            BinaryPrimitives.WriteUInt64BigEndian(value[sizeof(ulong)..], reclaimedThrough);
            metadata.PutSpan(key, value);
            _stampCache[address] = stampFrom;
        }
    }

    /// <inheritdoc/>
    public void OnPruningPassCompleted(ulong reclaimedThrough)
    {
        if (_slices.Count == 0 || dbProvider?.MetadataDb is not { } metadata) return;

        Span<byte> key = stackalloc byte[StampKeyPrefix.Length + Address.Size];
        StampKeyPrefix.CopyTo(key);
        Span<byte> value = stackalloc byte[StampValueLength];

        foreach (Address address in _slices.Keys)
        {
            address.Bytes.CopyTo(key[StampKeyPrefix.Length..]);
            if (metadata.Get(key) is not { Length: StampValueLength } stored) continue;

            ulong storedThrough = BinaryPrimitives.ReadUInt64BigEndian(stored.AsSpan(sizeof(ulong)));
            if (storedThrough >= reclaimedThrough) continue;

            stored.AsSpan(0, sizeof(ulong)).CopyTo(value);
            BinaryPrimitives.WriteUInt64BigEndian(value[sizeof(ulong)..], reclaimedThrough);
            metadata.PutSpan(key, value);
        }
    }

    /// <summary>Missing means refuse: an address no pruning pass has ever stamped has no proven depth - the
    /// pruner may simply not have run yet, and failing closed until it does costs minutes, not correctness.</summary>
    private ulong StampedRetainedFrom(AddressAsKey address)
    {
        if (_stampCache.TryGetValue(address, out ulong cached)) return cached;
        if (dbProvider?.MetadataDb is not { } metadata) return ulong.MaxValue;

        Span<byte> key = stackalloc byte[StampKeyPrefix.Length + Address.Size];
        StampKeyPrefix.CopyTo(key);
        address.Value.Bytes.CopyTo(key[StampKeyPrefix.Length..]);
        if (metadata.Get(key) is not { Length: StampValueLength } stored) return ulong.MaxValue;

        ulong stampFrom = BinaryPrimitives.ReadUInt64BigEndian(stored);
        _stampCache[address] = stampFrom;
        return stampFrom;
    }

    /// <inheritdoc/>
    /// <remarks>Deliberately the MINIMUM floor across bounded slices - the deepest window - because the cleanup
    /// cursor behind it is monotonic: a height must be outside every bounded window before the cursor passes it,
    /// or a deeper slice's band would be skipped while still retained and never revisited. A shallow slice's
    /// expired heights are therefore reclaimed late - once the deepest window's floor reaches them - never
    /// stranded.</remarks>
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
