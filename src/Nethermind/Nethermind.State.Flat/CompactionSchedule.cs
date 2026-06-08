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
    private readonly ulong _offset;

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

    // Changed from long → ulong to match the backing field type.
    // Callers that previously compared this to a long block-number should
    // now compare against a ulong block-number (BlockHeader.Number is ulong).
    public ulong Offset => _offset;

    public int GetCompactSize(ulong blockNumber)
    {
        if (_compactSize <= 1 || blockNumber == 0) return 1;
        ulong shifted = blockNumber + _offset;

        // C# has no unary minus for ulong, so we use the two's-complement
        // identity  x & -x  ≡  x & (~x + 1)  to isolate the lowest set bit.
        // No overflow risk: if shifted == 0 the guard above already returned.
        ulong lowestBit = shifted & (~shifted + 1UL);

        // Cast to int is safe: _compactSize is a power-of-2 int, so
        // Math.Min can never return a value larger than int.MaxValue.
        return (int)Math.Min(lowestBit, (ulong)_compactSize);
    }

    // Changed return type from long → ulong.
    // The sentinel "no compaction needed" value is now ulong.MaxValue instead
    // of long.MaxValue; callers should be updated accordingly.
    public ulong NextFullCompactionAfter(ulong from)
    {
        if (_compactSize <= 1) return ulong.MaxValue;

        if (from == ulong.MaxValue)
        {
            long fromSigned = -1;
            long offsetSigned = (long)_offset;
            long sizeSigned = (long)_compactSize;
            long modSigned = (fromSigned + offsetSigned) % sizeSigned;
            long distanceSigned = modSigned == 0 ? sizeSigned : sizeSigned - modSigned;
            return (ulong)(fromSigned + distanceSigned);
        }

        ulong size = (ulong)_compactSize;          // _compactSize is a small power-of-2, always fits
        ulong mod = (from + _offset) % size;      // all operands are ulong — no ambiguity
        ulong distance = mod == 0 ? size : size - mod;
        return from + distance;
    }

    // Changed return type from long → ulong so that _offset can be assigned
    // without a cast.  The on-disk RLP format is still encoded as long for
    // backward-compatibility with existing databases.
    private ulong ResolveOffset(IDb metadataDb, IFlatDbConfig config, ILogger logger)
    {
        if (_compactSize <= 1) return 0;

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

        // The persisted value was written as a long (see GenerateAndPersist).
        // Decode it as long first so we can detect corruption (negative values).
        long decoded = stored.AsRlpValueContext().DecodeLong();
        if (decoded < 0)
        {
            if (logger.IsWarn) logger.Warn($"Stored FlatDb compaction offset {decoded} is negative; regenerating");
            return GenerateAndPersist(metadataDb);
        }

        if (logger.IsInfo) logger.Info($"Loaded FlatDb compaction offset {decoded}");
        // Safe cast: we verified decoded >= 0 immediately above.
        return (ulong)decoded;
    }

    private ulong GenerateAndPersist(IDb metadataDb)
    {
        // Generate in the range [0, int.MaxValue) so the value fits in a
        // non-negative long AND the resulting ulong is well within range.
        long offset = Random.Shared.NextInt64(0, int.MaxValue);

        // Keep the on-disk encoding as long (RLP) for database compatibility.
        metadataDb.Set(MetadataDbKeys.FlatDbCompactionOffset, Rlp.Encode(offset).Bytes);

        // Safe cast: NextInt64(0, int.MaxValue) is always in [0, 2^31 − 1].
        return (ulong)offset;
    }
}
