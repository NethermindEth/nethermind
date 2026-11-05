// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Eez.Config;

namespace Nethermind.Eez.Sequencer;

/// <summary>
/// The block layout of one L1 slot. Each L1 block anchors K = L1 / L2 block time L2 blocks: Live blocks produced on the
/// wall clock, Future blocks produced ahead of it so the prover has <see cref="ProofTimeMs"/> before the next L1 block,
/// and one Sync block whose timestamp is that next L1 block's, which the postBatch settles in.
/// </summary>
/// <remarks>
/// <c>future = ceil((proof + slack) / L2) - 1</c>, <c>live = K - future - 1</c>, and the slot is composed at
/// <c>L1 - (future + 1) * L2</c> after the anchor L1 block.
/// </remarks>
public readonly record struct RollupTiming(uint L1BlockTimeMs, uint L2BlockTimeMs, uint ProofTimeMs, uint SubmissionSlackMs)
{
    /// <summary>The most blocks one catch-up composition produces, so its output settles in one postBatch.</summary>
    public const ulong MaxBlocksPerCatchup = 300;

    /// <summary>The least time between composing a slot and the last moment its proof may start, for producing the Sync block.</summary>
    public const uint MinComposeMarginMs = 100;

    public static RollupTiming From(IEezConfig config) =>
        new(config.L1BlockTimeMs, (uint)Math.Min(config.L2BlockTimeSeconds * 1000, uint.MaxValue), config.ProofTimeMs, config.SubmissionSlackMs);

    public uint K => L1BlockTimeMs / L2BlockTimeMs;

    public uint FutureCount
    {
        get
        {
            ulong budget = (ulong)ProofTimeMs + SubmissionSlackMs;
            ulong blocks = (budget + L2BlockTimeMs - 1) / L2BlockTimeMs;
            return blocks == 0 ? 0 : (uint)(blocks - 1);
        }
    }

    public uint LiveCount => K - FutureCount - 1;

    /// <summary>How long after the anchor L1 block the slot is composed: the end of its Live region.</summary>
    public TimeSpan ProofWindowOpen => TimeSpan.FromMilliseconds(L1BlockTimeMs - (FutureCount + 1UL) * L2BlockTimeMs);

    /// <returns>Why the timing cannot compose slots, or <see langword="null"/> when it can.</returns>
    public string? FindViolation()
    {
        if (L1BlockTimeMs == 0 || L2BlockTimeMs == 0 || ProofTimeMs == 0)
        {
            return "the L1 block time, L2 block time and proof time must be positive";
        }

        if (L2BlockTimeMs % 1000 != 0)
        {
            return $"the L2 block time ({L2BlockTimeMs} ms) must be whole seconds, as block timestamps are";
        }

        if (L1BlockTimeMs % L2BlockTimeMs != 0)
        {
            return $"the L1 block time ({L1BlockTimeMs} ms) must be a multiple of the L2 block time ({L2BlockTimeMs} ms)";
        }

        if (K < 2)
        {
            return $"an L1 slot must hold at least 2 L2 blocks, not {K}, so the Sync block is distinct from the Live ones";
        }

        ulong budget = (ulong)ProofTimeMs + SubmissionSlackMs;
        if (budget >= L1BlockTimeMs)
        {
            return $"the proof time ({ProofTimeMs} ms) plus the submission slack ({SubmissionSlackMs} ms) must be below the L1 block time ({L1BlockTimeMs} ms)";
        }

        ulong room = (K - 1UL) * L2BlockTimeMs;
        if (budget > room)
        {
            return $"the proof time ({ProofTimeMs} ms) plus the submission slack ({SubmissionSlackMs} ms) must fit the {room} ms of blocks before the Sync block";
        }

        ulong margin = (FutureCount + 1UL) * L2BlockTimeMs - budget;
        return margin < MinComposeMarginMs
            ? $"the proof time ({ProofTimeMs} ms) plus the submission slack ({SubmissionSlackMs} ms) must leave at least {MinComposeMarginMs} ms of the Future " +
              $"blocks to compose the Sync block in, not {margin} ms"
            : null;
    }

    /// <summary>What to produce when a slot is composed with the head at <paramref name="head"/>.</summary>
    /// <param name="syncHeight">The height of the slot's Sync block.</param>
    /// <param name="maxCatchup">The most blocks a catch-up may produce; its Sync block stays on the slot grid.</param>
    public SlotComposition Compose(ulong head, ulong syncHeight, ulong maxCatchup)
    {
        if (head >= syncHeight)
        {
            return SlotComposition.Idle;
        }

        ulong future = FutureCount;
        ulong k = K;
        ulong liveRegionEnd = syncHeight > future + 1 ? syncHeight - (future + 1) : 0;
        if (head >= liveRegionEnd)
        {
            ulong inFuture = head - liveRegionEnd;
            return SlotComposition.Slot(0, future > inFuture ? future - inFuture : 0);
        }

        ulong live = liveRegionEnd - head;
        if (live <= k - (future + 1))
        {
            return SlotComposition.Slot(live, future);
        }

        ulong gap = syncHeight - head;
        ulong over = gap > maxCatchup ? gap - maxCatchup : 0;
        ulong snap = (over + k - 1) / k * k;
        return snap >= syncHeight || syncHeight - snap <= head
            ? SlotComposition.Idle
            : SlotComposition.Catchup(syncHeight - snap - head - 1);
    }

    /// <summary>
    /// The last block of a bounded settlement: the highest height at most <paramref name="cap"/> above the cursor that
    /// is on <paramref name="syncHeight"/>'s slot grid, since derivation rebuilds a Sync block by its position.
    /// </summary>
    /// <returns><see langword="null"/> when no such height lies strictly between the cursor and the Sync height.</returns>
    public ulong? HistoricalChunkBoundary(ulong cursor, ulong syncHeight, ulong cap)
    {
        ulong k = K;
        ulong reach = cursor + cap;
        ulong over = syncHeight > reach ? syncHeight - reach : 0;
        ulong back = (over + k - 1) / k * k;
        if (back > syncHeight)
        {
            return null;
        }

        ulong boundary = syncHeight - back;
        return boundary > cursor && boundary < syncHeight ? boundary : null;
    }
}

public enum SlotCompositionKind
{
    /// <summary>The head is at or past the Sync height: nothing to produce.</summary>
    Idle,

    /// <summary><see cref="SlotComposition.Live"/> blocks, then an empty Sync block on the slot grid.</summary>
    Catchup,

    /// <summary><see cref="SlotComposition.Live"/> blocks, <see cref="SlotComposition.Future"/> blocks, then the Sync block.</summary>
    Slot,
}

/// <summary>The blocks one slot composition produces before its Sync block.</summary>
public readonly record struct SlotComposition(SlotCompositionKind Kind, ulong Live, ulong Future)
{
    public static SlotComposition Idle => default;

    public static SlotComposition Catchup(ulong live) => new(SlotCompositionKind.Catchup, live, 0);

    public static SlotComposition Slot(ulong live, ulong future) => new(SlotCompositionKind.Slot, live, future);

    /// <summary>Every block produced, the Sync block included.</summary>
    public ulong Blocks => Kind == SlotCompositionKind.Idle ? 0 : Live + Future + 1;
}
