// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Merge.AuRa.Shutter;
public static class ShutterHelpers
{
    public static (ulong slot, short slotOffset)? GetBuildingSlotAndOffset(ulong slotTimestampMs, ulong genesisTimestampMs, TimeSpan slotLength)
    {
        ulong slotTimeSinceGenesis = slotTimestampMs - genesisTimestampMs;
        ulong buildingSlot = slotTimeSinceGenesis / (ulong)slotLength.TotalMilliseconds;
        long offset = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)slotTimestampMs;
        return Math.Abs(offset) < (long)slotLength.TotalMilliseconds ? (buildingSlot, (short)offset) : null;
    }
}
