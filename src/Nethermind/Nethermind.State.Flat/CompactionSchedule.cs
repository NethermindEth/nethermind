// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Flat;

public sealed class CompactionSchedule : ICompactionSchedule
{
    private readonly ulong _compactSize;
    private readonly ulong _maxCompactSize;
    private readonly ulong _offset;

    public CompactionSchedule(
        [KeyFilter(DbNames.Metadata)] IDb metadataDb,
        IFlatDbConfig config,
        ILogManager logManager)
    {
        config.ValidateCompactSize();
        if (config.CompactSize > 1 && (config.CompactSize & (config.CompactSize - 1)) != 0)
            throw new ArgumentException("Compact size must be a power of 2");

        _compactSize = config.CompactSize;
        _maxCompactSize = config.PersistedSnapshotMaxCompactSize;

        ILogger logger = logManager.GetClassLogger<CompactionSchedule>();
        _offset = ResolveOffset(metadataDb, config, logger);
    }

    internal ulong Offset => _offset;

    public ulong GetCompactSize(ulong blockNumber)
    {
        if (_compactSize <= 1 || blockNumber == 0) return 1;
        return Math.Min(ShiftedAlignment(blockNumber), _compactSize);
    }

    public ulong NextFullCompactionAfter(ulong from)
    {
        if (_compactSize <= 1) return ulong.MaxValue;
        // Treat PreGenesis (ulong.MaxValue, "nothing persisted") as block 0 so the next boundary
        // is the first compaction boundary after genesis — keeping the finalized-persistence
        // trigger engaged at the start of sync instead of deferring to the force-persist backstop.
        if (from == ulong.MaxValue) from = 0;
        ulong mod = (from + _offset) % _compactSize;
        ulong distance = mod == 0 ? _compactSize : _compactSize - mod;
        // Overflow guard: a from near ulong.MaxValue is unreachable by a real chain, but keep the
        // addition from wrapping rather than returning a small bogus boundary.
        return from > ulong.MaxValue - distance ? ulong.MaxValue : from + distance;
    }

    // The methods below do NOT short-circuit on `_compactSize <= 1` (the "compaction
    // disabled" sentinel honoured by GetCompactSize and NextFullCompactionAfter), because
    // PersistedSnapshotCompactor runs with its own min/max caps and may legitimately
    // operate even when config.CompactSize == 1.

    public bool IsCompactSizeBoundary(ulong blockNumber) =>
        GetPersistedSnapshotCompactSize(blockNumber) == _compactSize;

    public bool IsLargeCompactionBoundary(ulong blockNumber) =>
        GetPersistedSnapshotCompactSize(blockNumber) > _compactSize;

    public ulong GetPersistedSnapshotCompactSize(ulong blockNumber) =>
        blockNumber == 0 ? 1 : Math.Min(ShiftedAlignment(blockNumber), _maxCompactSize);

    // x & (~x + 1) (two's-complement lowest-set-bit trick; -x is invalid for ulong): returns the
    // largest power of 2 dividing the offset-shifted block number, used by all boundary checks.
    private ulong ShiftedAlignment(ulong blockNumber)
    {
        ulong shifted = blockNumber + _offset;
        return shifted & (~shifted + 1UL);
    }

    private ulong ResolveOffset(IDb metadataDb, IFlatDbConfig config, ILogger logger)
    {
        if (_compactSize <= 1) return 0;

        if (config.CompactionOffset >= 0)
        {
            if (logger.IsInfo) logger.Info($"Using configured FlatDb compaction offset {config.CompactionOffset}");
            return (ulong)config.CompactionOffset;
        }

        if (config.RegenerateCompactionOffset)
        {
            ulong regenerated = GenerateAndPersist(metadataDb);
            if (logger.IsInfo) logger.Info($"Regenerated FlatDb compaction offset {regenerated} (RegenerateCompactionOffset=true)");
            return regenerated;
        }

        byte[]? stored = metadataDb.Get(MetadataDbKeys.FlatDbCompactionOffset);
        if (stored is null)
        {
            ulong generated = GenerateAndPersist(metadataDb);
            if (logger.IsInfo) logger.Info($"Generated new FlatDb compaction offset {generated}");
            return generated;
        }

        // On-disk RLP format uses long for backward compatibility; decode as long to detect corrupt negatives.
        long decoded = new RlpReader(stored).DecodeLong();
        if (decoded < 0)
        {
            if (logger.IsWarn) logger.Warn($"Stored FlatDb compaction offset {decoded} is negative; regenerating");
            return GenerateAndPersist(metadataDb);
        }

        if (logger.IsInfo) logger.Info($"Loaded FlatDb compaction offset {decoded}");
        return (ulong)decoded;
    }

    private ulong GenerateAndPersist(IDb metadataDb)
    {
        // Generate in [0, int.MaxValue) so the value encodes cleanly as a non-negative long
        // in the on-disk RLP format (kept as long for database backward compatibility).
        long offset = Random.Shared.NextInt64(0, int.MaxValue);
        metadataDb.Set(MetadataDbKeys.FlatDbCompactionOffset, Rlp.Encode(offset).Bytes);
        return (ulong)offset;
    }
}
