// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.State.Flat.History.Test.Archive;

/// <summary>How much chain the archive-index benchmark generates, and how the storage writes are spread over it.</summary>
/// <param name="Blocks">Blocks produced after genesis, each carrying one sweep transaction.</param>
/// <param name="SlotsPerBlock">Slots one block writes. Widening this is much cheaper than adding blocks.</param>
/// <param name="TotalSlots">Distinct slots the sweep cycles through, so a slot gets a new version every cycle.</param>
/// <param name="FlushEveryBlocks">
/// Blocks between forced persist+capture passes. Capture only runs when the flat state persists, so this is what
/// turns produced blocks into history rows.
/// </param>
public readonly record struct ArchiveChainShape(int Blocks, int SlotsPerBlock, int TotalSlots, int FlushEveryBlocks)
{
    private const string BlocksVariable = "NETHERMIND_ARCHIVE_BENCH_BLOCKS";
    private const string SlotsPerBlockVariable = "NETHERMIND_ARCHIVE_BENCH_SLOTS_PER_BLOCK";
    private const string TotalSlotsVariable = "NETHERMIND_ARCHIVE_BENCH_TOTAL_SLOTS";

    /// <summary>~1M history rows over 2 000 blocks. Minutes to build; the shape the published numbers use.</summary>
    public static ArchiveChainShape Benchmark => new(Blocks: 2_000, SlotsPerBlock: 500, TotalSlots: 20_000, FlushEveryBlocks: 64);

    /// <summary>Seconds to build. For correctness tests only — far too shallow to time anything.</summary>
    public static ArchiveChainShape Tiny => new(Blocks: 40, SlotsPerBlock: 20, TotalSlots: 200, FlushEveryBlocks: 8);

    /// <summary><see cref="Benchmark"/> with the three sizes overridable per run.</summary>
    public static ArchiveChainShape FromEnvironment()
    {
        ArchiveChainShape shape = Benchmark;
        return shape with
        {
            Blocks = ReadInt(BlocksVariable, shape.Blocks),
            SlotsPerBlock = ReadInt(SlotsPerBlockVariable, shape.SlotsPerBlock),
            TotalSlots = ReadInt(TotalSlotsVariable, shape.TotalSlots),
        };
    }

    /// <summary>Storage history rows the generation writes, before de-duplication by RocksDB.</summary>
    public long HistoryRows => (long)Blocks * SlotsPerBlock;

    /// <summary>Blocks the first cycle takes; below it some slots have no version yet.</summary>
    public int PrimingBlocks => TotalSlots / SlotsPerBlock;

    /// <summary>
    /// The block the benchmark calls at. Half way between the first full cycle and the head, so it is past priming
    /// and well below the barrier. <see cref="Validate"/> rejects shapes where it would land on the head, because
    /// there the "historical" call would read live flat state and quietly report the wrong thing.
    /// </summary>
    public ulong QueryBlock => (ulong)(PrimingBlocks + (Blocks - PrimingBlocks) / 2 + 1);

    /// <summary>
    /// Set at genesis and never adjusted afterwards, so the whole chain has room for one sweep per block.
    /// The bound divisor only lets the produced limit drift ~0.1% per block, which is far too slow to ramp up.
    /// </summary>
    public ulong BlockGasLimit => (ulong)StorageSweepContract.WriteGas(SlotsPerBlock) + 1_000_000;

    /// <summary>First slot the sweep in the given generated block writes.</summary>
    public ulong FirstSlotAt(int blockIndex) => (ulong)((long)blockIndex * SlotsPerBlock % TotalSlots);

    public void Validate()
    {
        if (Blocks <= 0 || SlotsPerBlock <= 0 || TotalSlots <= 0 || FlushEveryBlocks <= 0)
            throw new ArgumentOutOfRangeException(nameof(Blocks), this, "Every dimension must be positive.");

        // Otherwise a cycle straddles the wrap and the windows stop tiling, leaving slots with uneven version counts.
        if (TotalSlots % SlotsPerBlock != 0)
            throw new ArgumentOutOfRangeException(nameof(TotalSlots), this, $"{nameof(TotalSlots)} must be a multiple of {nameof(SlotsPerBlock)}.");

        // Without a full cycle some slots are never written and the read call would sum absent values.
        if (Blocks < PrimingBlocks * 2)
            throw new ArgumentOutOfRangeException(nameof(Blocks), this, $"{nameof(Blocks)} must cover at least two full cycles of {PrimingBlocks} blocks.");

        // Two cycles of one or two blocks each put the query block on the head, where nothing historical is read.
        if (QueryBlock <= (ulong)PrimingBlocks || QueryBlock >= (ulong)Blocks)
            throw new ArgumentOutOfRangeException(nameof(Blocks), this,
                $"{nameof(QueryBlock)} of {QueryBlock} must sit above the first cycle of {PrimingBlocks} blocks and " +
                $"below the head at block {Blocks}. Add blocks, or lower {nameof(SlotsPerBlock)} to shorten the cycle.");
    }

    private static int ReadInt(string variable, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(variable), out int value) ? value : fallback;
}
