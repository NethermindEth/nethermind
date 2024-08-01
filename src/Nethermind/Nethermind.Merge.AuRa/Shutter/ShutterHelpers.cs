// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Merge.AuRa.Shutter;
public static class ShutterHelpers
{
    public static (ulong slot, ulong slotOffset) GetBuildingSlotAndOffset(ulong slotTimestampMs, ulong genesisTimestampMs, TimeSpan slotLength)
    {
        ulong slotTimeSinceGenesis = slotTimestampMs - genesisTimestampMs;
        ulong buildingSlot = slotTimeSinceGenesis / (ulong)slotLength.TotalMilliseconds;
        ulong offset = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - slotTimestampMs;
        return (buildingSlot, offset);
    }
}
