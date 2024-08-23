// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Shutter;

public class ShutterTime(ulong genesisTimestamp, ITimestamper timestamper, TimeSpan slotLength, TimeSpan blockUpToDateCutoff)
{
    public class ShutterSlotCalulationException(string message, Exception? innerException = null) : Exception(message, innerException);
    public readonly ulong GenesisTimestamp = genesisTimestamp;

    public ulong GetGenesisTimestampMs() => 1000 * GenesisTimestamp;

    public ulong GetSlotTimestamp(ulong slot)
        => GenesisTimestamp + (ulong)slotLength.TotalSeconds * slot;

    public ulong GetSlotTimestampMs(ulong slot)
        => 1000 * GetSlotTimestamp(slot);

    public long GetCurrentOffsetMs(ulong slot, ulong? slotTimestampMs = null)
        => timestamper.UtcNowOffset.ToUnixTimeMilliseconds() - (long)(slotTimestampMs ?? GetSlotTimestampMs(slot));

    public bool IsBlockUpToDate(Block head)
        => timestamper.UtcNowOffset.ToUnixTimeSeconds() - (long)head.Header.Timestamp < blockUpToDateCutoff.TotalSeconds;

    public ulong GetSlot(ulong slotTimestampMs)
    {
        long slotTimeSinceGenesis = (long)slotTimestampMs - (long)GetGenesisTimestampMs();
        if (slotTimeSinceGenesis < 0)
        {
            throw new ShutterSlotCalulationException($"Slot timestamp {slotTimestampMs}ms was before than genesis timestamp {GetGenesisTimestampMs()}ms.");
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
