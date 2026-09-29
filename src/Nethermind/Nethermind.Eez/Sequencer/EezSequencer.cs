// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Eez.Follower;
using Nethermind.Eez.Posting;
using Nethermind.Logging;

namespace Nethermind.Eez.Sequencer;

/// <summary>Produces the rollup's blocks on the L1 slot schedule.</summary>
public interface IEezSequencer
{
    /// <summary>Does whatever is due now, given the latest L1 block the follower saw.</summary>
    /// <returns>When it next has something to do.</returns>
    Task<DateTimeOffset> Advance(FollowerHeads heads, EezL1Block? latestL1, CancellationToken token);
}

/// <summary>
/// Schedules the sequencer on the L1 slots. Each new L1 block N arms a slot whose Sync block has the timestamp of block
/// N + 1 and whose height follows from the genesis timestamp; a newer block replaces a slot not yet composed. Between
/// slots a Live block is produced every L2 block time while the head is below the slot's Live region. At N's timestamp
/// plus the proof window the slot is composed: the Live and Future blocks still missing, then the Sync block, or a
/// grid-aligned catch-up when the head is far behind. A slot too late for its proof gets an empty Sync block and no
/// batch, and a head that is not on the slot's grid skips it.
/// </summary>
public sealed class EezSequencer(
    RollupTiming timing,
    ulong genesisTimestamp,
    ISequencedBlocks blocks,
    ISyncSlotComposer composer,
    ITimestamper clock,
    ulong maxSpeculativeDepth,
    ILogManager logManager) : IEezSequencer
{
    private const int OffGridAlarm = 3;

    private readonly ILogger _logger = logManager.GetClassLogger<EezSequencer>();
    private readonly ulong _l2BlockSeconds = timing.L2BlockTimeMs / 1000;
    private Slot? _slot;
    private DateTimeOffset _nextLiveTick;
    private int _offGridSkips;

    public async Task<DateTimeOffset> Advance(FollowerHeads heads, EezL1Block? latestL1, CancellationToken token)
    {
        if (latestL1 is { } l1 && (_slot is null || l1.Number > _slot.Anchor.Number || (l1.Number == _slot.Anchor.Number && l1.Hash != _slot.Anchor.Hash)))
        {
            Arm(l1);
        }

        DateTimeOffset now = clock.UtcNowOffset;
        if (_slot is { Composed: false } slot && now >= slot.ComposeAt)
        {
            slot.Composed = true;
            await Compose(slot, heads, token);
        }
        else if (now >= _nextLiveTick)
        {
            _nextLiveTick = now + TimeSpan.FromMilliseconds(timing.L2BlockTimeMs);
            await LiveTick(heads, token);
        }

        return _slot is { Composed: false } next && next.ComposeAt < _nextLiveTick ? next.ComposeAt : _nextLiveTick;
    }

    private void Arm(EezL1Block anchor)
    {
        if (_slot is { Composed: false } superseded && _logger.IsWarn)
        {
            _logger.Warn($"The slot anchored at L1 block {superseded.Anchor.Number} was not composed before block {anchor.Number} arrived.");
        }

        ulong syncTimestamp = anchor.Timestamp + timing.L1BlockTimeMs / 1000;
        ulong syncHeight = syncTimestamp > genesisTimestamp ? (syncTimestamp - genesisTimestamp) / _l2BlockSeconds : 0;
        _slot = new Slot(anchor, DateTimeOffset.FromUnixTimeSeconds((long)anchor.Timestamp) + timing.ProofWindowOpen, syncTimestamp, syncHeight);
    }

    /// <summary>One Live block, while the head is below the current slot's Live region.</summary>
    private async Task LiveTick(FollowerHeads heads, CancellationToken token)
    {
        if (_slot is not { } slot || heads.Head.Number + timing.FutureCount + 1 >= slot.SyncHeight || AtDepthCap(heads))
        {
            return;
        }

        await blocks.Live(heads.Head, heads, token);
    }

    private async Task Compose(Slot slot, FollowerHeads heads, CancellationToken token)
    {
        BlockHeader head = heads.Head;
        SlotComposition composition = timing.Compose(head.Number, slot.SyncHeight, RollupTiming.MaxBlocksPerCatchup);
        switch (composition.Kind)
        {
            case SlotCompositionKind.Idle:
                return;
            case SlotCompositionKind.Catchup:
                ulong terminal = head.Number + composition.Blocks;
                for (ulong i = 0; i < composition.Live; i++)
                {
                    head = await blocks.Live(head, heads, token);
                }

                if (head.Number + 1 != terminal)
                {
                    SkipOffGrid(slot, $"the head moved to {head.Number} during the catch-up to {terminal}");
                    return;
                }

                await composer.Compose(head, BundleTarget.NextBlock, SyncSlotMode.Catchup, heads, token);
                _offGridSkips = 0;
                return;
            default:
                for (ulong i = 0; i < composition.Live + composition.Future; i++)
                {
                    if (AtDepthCap(heads))
                    {
                        return;
                    }

                    head = await blocks.Live(head, heads, token);
                }

                if (head.Timestamp + _l2BlockSeconds != slot.SyncTimestamp)
                {
                    SkipOffGrid(slot, $"the head's child would have timestamp {head.Timestamp + _l2BlockSeconds}, not {slot.SyncTimestamp}");
                    return;
                }

                if (AtDepthCap(heads))
                {
                    return;
                }

                bool late = clock.UtcNowOffset.ToUnixTimeMilliseconds() + timing.ProofTimeMs > (long)slot.SyncTimestamp * 1000 - timing.SubmissionSlackMs;
                await composer.Compose(head, late ? BundleTarget.NextBlock : new BundleTarget(slot.Anchor.Number + 1, slot.SyncTimestamp),
                    late ? SyncSlotMode.Empty : SyncSlotMode.Steady, heads, token);
                _offGridSkips = 0;
                return;
        }
    }

    /// <summary>Whether the head is as far above what L1 settled as the sequencer may build.</summary>
    private bool AtDepthCap(FollowerHeads heads) => maxSpeculativeDepth != 0 && heads.Head.Number - heads.Safe.Number >= maxSpeculativeDepth;

    private void SkipOffGrid(Slot slot, string reason)
    {
        _offGridSkips++;
        if (_offGridSkips >= OffGridAlarm)
        {
            if (_logger.IsError) _logger.Error($"{_offGridSkips} slots in a row were skipped off the grid; the last, for Sync block {slot.SyncHeight}, because {reason}.");
        }
        else if (_logger.IsWarn)
        {
            _logger.Warn($"Skipping the Sync block of slot {slot.SyncHeight}: {reason}.");
        }
    }

    private sealed class Slot(EezL1Block anchor, DateTimeOffset composeAt, ulong syncTimestamp, ulong syncHeight)
    {
        public EezL1Block Anchor { get; } = anchor;

        public DateTimeOffset ComposeAt { get; } = composeAt;

        public ulong SyncTimestamp { get; } = syncTimestamp;

        public ulong SyncHeight { get; } = syncHeight;

        public bool Composed { get; set; }
    }
}
