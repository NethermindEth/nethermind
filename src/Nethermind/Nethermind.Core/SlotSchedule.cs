// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core;

/// <summary>
/// The beacon slot grid as the execution layer sees it: slots of the configured length from the beacon genesis,
/// then slots of the chain's EIP-8198 slot duration (<see cref="Specs.ISpecProvider.Eip8198SlotDurationMs"/>) from the fork.
/// </summary>
/// <remarks>
/// Mirrors the consensus layer's <c>compute_slot_at_time_ms</c> and <c>compute_time_at_slot_ms</c> (consensus-specs
/// EIP-8198): the fork starts at the first slot boundary at or after its activation timestamp, which is the boundary
/// itself on a correctly scheduled chain. Without a beacon genesis only durations and steps are known, so the fork
/// starts at its activation timestamp and slot numbers are unavailable. Without EIP-8198 every slot has the
/// configured length.
/// </remarks>
public sealed class SlotSchedule
{
    private readonly ulong _secondsPerSlot;
    private readonly ulong _eip8198SecondsPerSlot;
    private readonly ulong? _genesisTimestamp;
    private readonly bool _hasFork;
    private readonly ulong _forkTimestamp;
    private readonly ulong _forkSlot;

    /// <param name="secondsPerSlot">Slot length before EIP-8198; zero is treated as one second.</param>
    /// <param name="genesisTimestamp">Beacon chain genesis timestamp, when known.</param>
    /// <param name="eip8198Timestamp">EIP-8198 activation timestamp, when scheduled.</param>
    /// <param name="eip8198SlotDurationMs">Slot length from EIP-8198, in milliseconds; see <see cref="ValidateSlotDurationMs"/>.</param>
    public SlotSchedule(
        ulong secondsPerSlot,
        ulong? genesisTimestamp = null,
        ulong? eip8198Timestamp = null,
        ulong eip8198SlotDurationMs = Eip8198Constants.DefaultSlotDurationMs)
    {
        ValidateSlotDurationMs(eip8198SlotDurationMs);
        _secondsPerSlot = Math.Max(1UL, secondsPerSlot);
        _eip8198SecondsPerSlot = eip8198SlotDurationMs / MillisecondsPerSecond;
        _genesisTimestamp = genesisTimestamp;
        if (eip8198Timestamp is not { } fork) return;

        _hasFork = true;
        if (genesisTimestamp is { } genesis)
        {
            _forkSlot = fork <= genesis ? 0 : CeilDiv(fork - genesis, _secondsPerSlot);
            _forkTimestamp = genesis + _forkSlot * _secondsPerSlot;
        }
        else
        {
            _forkTimestamp = fork;
        }
    }

    /// <summary>The length of the slot that starts at, or contains, <paramref name="timestamp"/>.</summary>
    public ulong GetSecondsPerSlot(ulong timestamp) =>
        IsAfterFork(timestamp) ? _eip8198SecondsPerSlot : _secondsPerSlot;

    /// <summary>The timestamp one slot after <paramref name="timestamp"/>.</summary>
    public ulong GetNextSlotTimestamp(ulong timestamp) => AddSlots(timestamp, 1);

    /// <summary>The timestamp <paramref name="slots"/> slots after <paramref name="timestamp"/>.</summary>
    /// <remarks>Each step has the length of the slot it starts in, so steps from before the fork switch length once past it.</remarks>
    public ulong AddSlots(ulong timestamp, ulong slots)
    {
        if (!_hasFork) return timestamp + slots * _secondsPerSlot;
        if (timestamp >= _forkTimestamp) return timestamp + slots * _eip8198SecondsPerSlot;

        ulong stepsToFork = CeilDiv(_forkTimestamp - timestamp, _secondsPerSlot);
        return slots <= stepsToFork
            ? timestamp + slots * _secondsPerSlot
            : timestamp + stepsToFork * _secondsPerSlot + (slots - stepsToFork) * _eip8198SecondsPerSlot;
    }

    /// <summary>The first timestamp at least one slot after <paramref name="timestamp"/> and not before <paramref name="target"/>.</summary>
    public ulong AddSlotsUntil(ulong timestamp, ulong target)
    {
        if (!_hasFork) return timestamp + StepsTo(timestamp, target, _secondsPerSlot) * _secondsPerSlot;
        if (timestamp >= _forkTimestamp) return timestamp + StepsTo(timestamp, target, _eip8198SecondsPerSlot) * _eip8198SecondsPerSlot;

        ulong stepsToFork = CeilDiv(_forkTimestamp - timestamp, _secondsPerSlot);
        ulong steps = StepsTo(timestamp, target, _secondsPerSlot);
        if (steps <= stepsToFork) return timestamp + steps * _secondsPerSlot;

        ulong atFork = timestamp + stepsToFork * _secondsPerSlot;
        return atFork + CeilDiv(target - atFork, _eip8198SecondsPerSlot) * _eip8198SecondsPerSlot;
    }

    /// <summary>The slot containing <paramref name="timestamp"/>; false without a beacon genesis or before it.</summary>
    public bool TryGetSlot(ulong timestamp, out ulong slot)
    {
        if (_genesisTimestamp is not { } genesis || timestamp < genesis)
        {
            slot = 0;
            return false;
        }

        slot = !IsAfterFork(timestamp)
            ? (timestamp - genesis) / _secondsPerSlot
            : _forkSlot + (timestamp - _forkTimestamp) / _eip8198SecondsPerSlot;
        return true;
    }

    /// <summary>The start of <paramref name="slot"/>; null without a beacon genesis.</summary>
    public ulong? GetSlotTimestamp(ulong slot) =>
        _genesisTimestamp is not { } genesis ? null
        : !_hasFork || slot <= _forkSlot ? genesis + slot * _secondsPerSlot
        : _forkTimestamp + (slot - _forkSlot) * _eip8198SecondsPerSlot;

    /// <summary>Whether <paramref name="timestamp"/> is a slot boundary; false without a beacon genesis.</summary>
    public bool IsSlotStart(ulong timestamp) => TryGetSlot(timestamp, out ulong slot) && GetSlotTimestamp(slot) == timestamp;

    /// <summary>
    /// Throws unless <paramref name="slotDurationMs"/> is a positive whole number of seconds: execution-layer timestamps are
    /// in seconds, so a fractional slot length would put slot boundaries between representable timestamps.
    /// </summary>
    public static void ValidateSlotDurationMs(ulong slotDurationMs)
    {
        if (slotDurationMs == 0 || slotDurationMs % MillisecondsPerSecond != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slotDurationMs),
                slotDurationMs,
                $"EIP-8198 slot duration must be a positive multiple of {MillisecondsPerSecond} ms, since execution-layer timestamps are whole seconds.");
        }
    }

    private const ulong MillisecondsPerSecond = 1_000;

    private bool IsAfterFork(ulong timestamp) => _hasFork && timestamp >= _forkTimestamp;

    private static ulong StepsTo(ulong timestamp, ulong target, ulong secondsPerSlot) =>
        target > timestamp ? Math.Max(1UL, CeilDiv(target - timestamp, secondsPerSlot)) : 1UL;

    private static ulong CeilDiv(ulong value, ulong divisor) => value / divisor + (value % divisor == 0 ? 0UL : 1UL);
}
