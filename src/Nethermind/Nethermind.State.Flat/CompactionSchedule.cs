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
    private readonly int _compactSize;
    private readonly long _offset;

    public CompactionSchedule(
        [KeyFilter(DbNames.Metadata)] IDb metadataDb,
        IFlatDbConfig config,
        ILogManager logManager)
    {
        if (config.CompactSize > 1 && (config.CompactSize & (config.CompactSize - 1)) != 0)
            throw new ArgumentException("Compact size must be a power of 2");

        _compactSize = config.CompactSize;

        ILogger logger = logManager.GetClassLogger<CompactionSchedule>();
        _offset = ResolveOffset(metadataDb, config, logger);
    }

    public long Offset => _offset;

    public int GetCompactSize(long blockNumber)
    {
        if (_compactSize <= 1 || blockNumber == 0) return 1;
        long shifted = blockNumber + _offset;
        return (int)Math.Min(shifted & -shifted, _compactSize);
    }

    public long NextFullCompactionAfter(long from)
    {
        if (_compactSize <= 1) return long.MaxValue;
        long mod = (from + _offset) % _compactSize;
        long distance = mod == 0 ? _compactSize : _compactSize - mod;
        return from + distance;
    }

    private long ResolveOffset(IDb metadataDb, IFlatDbConfig config, ILogger logger)
    {
        if (_compactSize <= 1) return 0;

        if (config.CompactionOffset >= 0)
        {
            if (logger.IsInfo) logger.Info($"Using configured FlatDb compaction offset {config.CompactionOffset}");
            return config.CompactionOffset;
        }

        if (config.RegenerateCompactionOffset)
        {
            long regenerated = GenerateAndPersist(metadataDb);
            if (logger.IsInfo) logger.Info($"Regenerated FlatDb compaction offset {regenerated} (RegenerateCompactionOffset=true)");
            return regenerated;
        }

        byte[]? stored = metadataDb.Get(MetadataDbKeys.FlatDbCompactionOffset);
        if (stored is null)
        {
            long generated = GenerateAndPersist(metadataDb);
            if (logger.IsInfo) logger.Info($"Generated new FlatDb compaction offset {generated}");
            return generated;
        }

        long decoded = stored.AsRlpValueContext().DecodeLong();
        if (decoded < 0)
        {
            if (logger.IsWarn) logger.Warn($"Stored FlatDb compaction offset {decoded} is negative; regenerating");
            return GenerateAndPersist(metadataDb);
        }

        if (logger.IsInfo) logger.Info($"Loaded FlatDb compaction offset {decoded}");
        return decoded;
    }

    private long GenerateAndPersist(IDb metadataDb)
    {
        long offset = Random.Shared.NextInt64(0, int.MaxValue);
        metadataDb.Set(MetadataDbKeys.FlatDbCompactionOffset, Rlp.Encode(offset).Bytes);
        return offset;
    }
}
