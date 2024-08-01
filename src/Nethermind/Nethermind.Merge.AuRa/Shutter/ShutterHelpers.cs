// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Logging;

namespace Nethermind.Merge.AuRa.Shutter;
public static class ShutterHelpers
{
    public class ShutterSlotCalulationException(string message, Exception? innerException = null) : Exception(message, innerException);

    public static (ulong slot, short slotOffset) GetBuildingSlotAndOffset(ulong slotTimestampMs, ulong genesisTimestampMs, TimeSpan slotLength)
    {
        long slotTimeSinceGenesis = (long)slotTimestampMs - (long)genesisTimestampMs;
        if (slotTimeSinceGenesis < 0)
        {
            throw new ShutterSlotCalulationException($"Slot timestamp {slotTimestampMs}ms was greater than genesis timestamp {genesisTimestampMs}ms.");
        }

        ulong buildingSlot = (ulong)slotTimeSinceGenesis / (ulong)slotLength.TotalMilliseconds;
        long offset = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)slotTimestampMs;
        if (Math.Abs(offset) >= (long)slotLength.TotalMilliseconds)
        {
            throw new ShutterSlotCalulationException($"Time offset {offset}ms into building slot {buildingSlot} was out of valid range.");
        }

        return (buildingSlot, (short)offset);
    }
}
