// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core;

public class SlotTime(ulong genesisTimestampMs, ITimestamper timestamper, TimeSpan slotLength, TimeSpan blockUpToDateCutoff)
{
    public class SlotCalulationException(string message, Exception? innerException = null) : Exception(message, innerException);

    public readonly ulong GenesisTimestampMs = genesisTimestampMs;

    public ulong GetSlotTimestampMs(ulong slot)
        => GenesisTimestampMs + slot * (ulong)slotLength.TotalMilliseconds;

    public long GetCurrentOffsetMs(ulong slot, ulong? slotTimestampMs = null)
        => timestamper.UtcNowOffset.ToUnixTimeMilliseconds() - (long)(slotTimestampMs ?? GetSlotTimestampMs(slot));

    public bool IsBlockUpToDate(Block head)
        => timestamper.UtcNowOffset.ToUnixTimeSeconds() - (long)head.Header.Timestamp < blockUpToDateCutoff.TotalSeconds;

    public ulong GetSlot(ulong slotTimestampMs)
    {
        long slotTimeSinceGenesis = (long)slotTimestampMs - (long)GenesisTimestampMs;
        if (slotTimeSinceGenesis < 0)
        {
            throw new SlotCalulationException($"Slot timestamp {slotTimestampMs}ms was before than genesis timestamp {GenesisTimestampMs}ms.");
        }

        return (ulong)slotTimeSinceGenesis / (ulong)slotLength.TotalMilliseconds;
    }

    public (ulong slot, long slotOffset) GetBuildingSlotAndOffset(ulong slotTimestampMs)
    {
        ulong buildingSlot = GetSlot(slotTimestampMs);
        long offset = GetCurrentOffsetMs(buildingSlot, slotTimestampMs);

        return (buildingSlot, offset);
    }
}
