// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Core;

/// <summary>
/// The beacon slot grid as the execution layer sees it: slots of the configured length from the beacon genesis, then
/// slots of each slot length change from its timestamp.
/// </summary>
/// <remarks>
/// Mirrors the consensus layer's <c>compute_slot_at_time_ms</c> and <c>compute_time_at_slot_ms</c> over
/// <c>SLOT_DURATION_SCHEDULE</c> (consensus-specs EIP-8198): each change starts at the first slot boundary at or after its
/// timestamp, which is the timestamp itself on a correctly scheduled chain, and a change at or before the beacon genesis
/// applies from genesis. Without a beacon genesis only durations and steps are known, so each change starts at its
/// timestamp and slot numbers are unavailable. Without changes every slot has the configured length.
/// </remarks>
public sealed class SlotSchedule
{
    private const ulong MillisecondsPerSecond = 1_000;

    private readonly ulong? _genesisTimestamp;
    private readonly Segment[] _segments;

    /// <summary>A grid where every slot has the configured length.</summary>
    /// <param name="secondsPerSlot">Slot length; zero is treated as one second.</param>
    /// <param name="genesisTimestamp">Beacon chain genesis timestamp, when known.</param>
    public SlotSchedule(ulong secondsPerSlot, ulong? genesisTimestamp = null) : this(secondsPerSlot, genesisTimestamp, []) { }

    /// <summary>A grid that changes slot length at each of <paramref name="schedule"/>.</summary>
    /// <param name="secondsPerSlot">Slot length before the first change; zero is treated as one second.</param>
    /// <param name="genesisTimestamp">Beacon chain genesis timestamp, when known.</param>
    /// <param name="schedule">Slot length changes; see <see cref="ValidateSchedule"/>.</param>
    public SlotSchedule(ulong secondsPerSlot, ulong? genesisTimestamp, IReadOnlyList<(ulong Timestamp, ulong SlotDurationMs)> schedule)
    {
        ValidateSchedule(schedule);
        _genesisTimestamp = genesisTimestamp;

        List<Segment> segments = [new(genesisTimestamp ?? 0, 0, Math.Max(1UL, secondsPerSlot))];
        foreach ((ulong Timestamp, ulong SlotDurationMs) entry in schedule)
        {
            Segment last = segments[^1];
            ulong seconds = entry.SlotDurationMs / MillisecondsPerSecond;
            if (entry.Timestamp <= last.Start)
            {
                segments[^1] = last with { Seconds = seconds };
                continue;
            }

            if (genesisTimestamp is null)
            {
                segments.Add(new(entry.Timestamp, 0, seconds));
                continue;
            }

            ulong slots = CeilDiv(entry.Timestamp - last.Start, last.Seconds);
            segments.Add(new(last.Start + slots * last.Seconds, last.StartSlot + slots, seconds));
        }

        _segments = [.. segments];
    }

    /// <summary>The length of the slot that starts at, or contains, <paramref name="timestamp"/>.</summary>
    public ulong GetSecondsPerSlot(ulong timestamp) => _segments[SegmentAt(timestamp)].Seconds;

    /// <summary>The timestamp one slot after <paramref name="timestamp"/>.</summary>
    public ulong GetNextSlotTimestamp(ulong timestamp) => AddSlots(timestamp, 1);

    /// <summary>The timestamp <paramref name="slots"/> slots after <paramref name="timestamp"/>.</summary>
    /// <remarks>Each step has the length of the slot it starts in, so steps switch length once past a schedule boundary.</remarks>
    public ulong AddSlots(ulong timestamp, ulong slots)
    {
        for (int i = SegmentAt(timestamp); ; i = SegmentAt(timestamp))
        {
            ulong seconds = _segments[i].Seconds;
            if (i + 1 == _segments.Length) return timestamp + slots * seconds;

            ulong stepsToNext = CeilDiv(_segments[i + 1].Start - timestamp, seconds);
            if (slots <= stepsToNext) return timestamp + slots * seconds;

            timestamp += stepsToNext * seconds;
            slots -= stepsToNext;
        }
    }

    /// <summary>The first timestamp at least one slot after <paramref name="timestamp"/> and not before <paramref name="target"/>.</summary>
    public ulong AddSlotsUntil(ulong timestamp, ulong target)
    {
        ulong minSteps = 1;
        for (int i = SegmentAt(timestamp); ; i = SegmentAt(timestamp))
        {
            ulong seconds = _segments[i].Seconds;
            ulong steps = Math.Max(minSteps, target > timestamp ? CeilDiv(target - timestamp, seconds) : 0);
            if (i + 1 == _segments.Length) return timestamp + steps * seconds;

            ulong stepsToNext = CeilDiv(_segments[i + 1].Start - timestamp, seconds);
            if (steps <= stepsToNext) return timestamp + steps * seconds;

            timestamp += stepsToNext * seconds;
            minSteps = 0;
        }
    }

    /// <summary>The slot containing <paramref name="timestamp"/>; false without a beacon genesis or before it.</summary>
    public bool TryGetSlot(ulong timestamp, out ulong slot)
    {
        if (_genesisTimestamp is not { } genesis || timestamp < genesis)
        {
            slot = 0;
            return false;
        }

        Segment segment = _segments[SegmentAt(timestamp)];
        slot = segment.StartSlot + (timestamp - segment.Start) / segment.Seconds;
        return true;
    }

    /// <summary>The start of <paramref name="slot"/>; null without a beacon genesis.</summary>
    public ulong? GetSlotTimestamp(ulong slot)
    {
        if (_genesisTimestamp is null) return null;

        int i = _segments.Length - 1;
        while (_segments[i].StartSlot > slot) i--;
        Segment segment = _segments[i];
        return segment.Start + (slot - segment.StartSlot) * segment.Seconds;
    }

    /// <summary>Whether <paramref name="timestamp"/> is a slot boundary; false without a beacon genesis.</summary>
    public bool IsSlotStart(ulong timestamp) => TryGetSlot(timestamp, out ulong slot) && GetSlotTimestamp(slot) == timestamp;

    /// <summary>
    /// Throws unless the changes are strictly increasing by timestamp and each duration passes
    /// <see cref="ValidateSlotDurationMs"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The schedule is invalid.</exception>
    public static void ValidateSchedule(IReadOnlyList<(ulong Timestamp, ulong SlotDurationMs)> schedule)
    {
        for (int i = 0; i < schedule.Count; i++)
        {
            (ulong Timestamp, ulong SlotDurationMs) entry = schedule[i];
            if (i > 0 && entry.Timestamp <= schedule[i - 1].Timestamp)
            {
                throw new ArgumentException(
                    $"Slot length changes must be strictly increasing by timestamp, but {entry.Timestamp} follows {schedule[i - 1].Timestamp}.",
                    nameof(schedule));
            }

            ValidateSlotDurationMs(entry.SlotDurationMs);
        }
    }

    /// <summary>
    /// Throws unless <paramref name="slotDurationMs"/> is a positive whole number of seconds, since execution-layer timestamps are
    /// in seconds.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The duration is zero or not whole seconds.</exception>
    public static void ValidateSlotDurationMs(ulong slotDurationMs)
    {
        if (slotDurationMs == 0 || slotDurationMs % MillisecondsPerSecond != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slotDurationMs),
                slotDurationMs,
                $"Slot duration must be a positive multiple of {MillisecondsPerSecond} ms, since execution-layer timestamps are whole seconds.");
        }
    }

    private int SegmentAt(ulong timestamp)
    {
        int i = _segments.Length - 1;
        while (i > 0 && _segments[i].Start > timestamp) i--;
        return i;
    }

    private static ulong CeilDiv(ulong value, ulong divisor) => value / divisor + (value % divisor == 0 ? 0UL : 1UL);

    /// <param name="Start">First timestamp of the segment; a slot boundary when the beacon genesis is known.</param>
    /// <param name="StartSlot">Slot starting at <paramref name="Start"/>; zero without a beacon genesis.</param>
    /// <param name="Seconds">Slot length within the segment.</param>
    private readonly record struct Segment(ulong Start, ulong StartSlot, ulong Seconds);
}
