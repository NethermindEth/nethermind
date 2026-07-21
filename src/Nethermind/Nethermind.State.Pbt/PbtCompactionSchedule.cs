// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Numerics;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.State.Pbt;

/// <summary>
/// Decides which blocks compact, and how wide. The width at a block is the largest power of two
/// dividing it, capped at <see cref="IPbtConfig.CompactSize"/> — so every other block merges 2 layers,
/// every fourth merges 4, and so on up to the cap, each level consuming the level below it.
/// </summary>
/// <remarks>
/// A per-node offset shifts the whole schedule. Without it every node on the network would compact
/// and persist on exactly the same blocks, so the same instant would be expensive everywhere at once.
/// It is persisted, because a node that regenerated it on restart would compact against boundaries
/// its own on-disk state was not built around.
/// </remarks>
public sealed class PbtCompactionSchedule
{
    private readonly ulong _compactSize;
    private readonly ulong _offset;

    public PbtCompactionSchedule([KeyFilter(DbNames.Metadata)] IDb metadataDb, IPbtConfig config, ILogManager logManager)
    {
        // the whole schedule is the lowest-set-bit of the shifted block number, which only nests into
        // levels when the cap is itself a power of two
        if (config.CompactSize > 1 && !BitOperations.IsPow2(config.CompactSize))
        {
            throw new ArgumentException($"{nameof(IPbtConfig.CompactSize)} must be a power of 2, was {config.CompactSize}", nameof(config));
        }

        _compactSize = (ulong)config.CompactSize;
        _offset = ResolveOffset(metadataDb, config, logManager.GetClassLogger<PbtCompactionSchedule>());
    }

    internal ulong Offset => _offset;

    /// <summary>How many layers the compaction at <paramref name="blockNumber"/> merges; 1 when none should run.</summary>
    public ulong GetCompactSize(ulong blockNumber)
    {
        if (_compactSize <= 1 || blockNumber == 0) return 1;

        // x & (~x + 1): the two's-complement lowest-set-bit trick, spelled without unary minus because
        // that is not defined for ulong.
        ulong shifted = blockNumber + _offset;
        return Math.Min(shifted & (~shifted + 1UL), _compactSize);
    }

    /// <summary>The next block after <paramref name="from"/> that merges a full <see cref="IPbtConfig.CompactSize"/> window, and so is a persistence boundary.</summary>
    /// <remarks>
    /// <see cref="StateId.PreGenesis"/> counts as block 0, so an empty database aims at the first
    /// boundary after genesis rather than at the "no further boundary" sentinel.
    /// </remarks>
    public ulong NextFullCompactionAfter(in StateId from)
    {
        if (_compactSize <= 1) return ulong.MaxValue;

        ulong blockNumber = from == StateId.PreGenesis ? 0UL : from.BlockNumber;
        ulong mod = (blockNumber + _offset) % _compactSize;
        ulong distance = mod == 0 ? _compactSize : _compactSize - mod;

        // a chain never reaches here, but wrapping would return a boundary below the block we started from
        return blockNumber > ulong.MaxValue - distance ? ulong.MaxValue : blockNumber + distance;
    }

    private ulong ResolveOffset(IDb metadataDb, IPbtConfig config, ILogger logger)
    {
        if (_compactSize <= 1) return 0;

        if (config.CompactionOffset >= 0)
        {
            if (logger.IsInfo) logger.Info($"Using configured pbt compaction offset {config.CompactionOffset}");
            return (ulong)config.CompactionOffset;
        }

        byte[]? stored = metadataDb.Get(MetadataDbKeys.PbtCompactionOffset);
        if (stored?.Length == sizeof(ulong))
        {
            ulong loaded = BinaryPrimitives.ReadUInt64LittleEndian(stored);
            if (logger.IsInfo) logger.Info($"Loaded pbt compaction offset {loaded}");
            return loaded;
        }

        ulong generated = (ulong)Random.Shared.NextInt64(0, int.MaxValue);
        byte[] encoded = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(encoded, generated);
        metadataDb.Set(MetadataDbKeys.PbtCompactionOffset, encoded);
        if (logger.IsInfo) logger.Info($"Generated pbt compaction offset {generated}");
        return generated;
    }
}
