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
    private readonly ulong _offset;

    public CompactionSchedule(
        [KeyFilter(DbNames.Metadata)] IDb metadataDb,
        IFlatDbConfig config,
        ILogManager logManager)
    {
        int cs = config.CompactSize;
        if (cs > 1 && (cs & (cs - 1)) != 0)
            throw new ArgumentException("Compact size must be a power of 2");

        _compactSize = (ulong)cs;

        ILogger logger = logManager.GetClassLogger<CompactionSchedule>();
        _offset = ResolveOffset(metadataDb, config, logger);
    }

    public ulong Offset => _offset;

    public int GetCompactSize(ulong blockNumber)
    {
        if (_compactSize <= 1 || blockNumber == 0) return 1;
        ulong shifted = blockNumber + _offset;

        // Isolate the lowest set bit via two's-complement identity: x & (~x + 1).
        ulong lowestBit = shifted & (~shifted + 1UL);

        return (int)Math.Min(lowestBit, _compactSize);
    }

    public ulong NextFullCompactionAfter(ulong from)
    {
        if (_compactSize <= 1) return ulong.MaxValue;
        if (from == ulong.MaxValue) return ulong.MaxValue;

        ulong mod = (from + _offset) % _compactSize;
        ulong distance = mod == 0 ? _compactSize : _compactSize - mod;
        return from > ulong.MaxValue - distance ? ulong.MaxValue : from + distance;
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
