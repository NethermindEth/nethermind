// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Specs;

namespace Nethermind.Shutter;
public static class ShutterHelpers
{
    public static TimeSpan SlotLength => GnosisSpecProvider.SlotLength;
    public class ShutterSlotCalulationException(string message, Exception? innerException = null) : Exception(message, innerException);

    public static ulong GetGenesisTimestampMs(ISpecProvider specProvider) => 1000 * (specProvider.ChainId == BlockchainIds.Chiado ? ChiadoSpecProvider.BeaconChainGenesisTimestamp : GnosisSpecProvider.BeaconChainGenesisTimestamp);

    public static ulong GetSlotTimestampMs(ulong slot, ulong genesisTimestampMs)
        => genesisTimestampMs + (ulong)SlotLength.TotalMilliseconds * slot;

    public static long GetCurrentOffsetMs(ulong slot, ulong genesisTimestampMs, ulong? slotTimestampMs = null)
        => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)(slotTimestampMs ?? GetSlotTimestampMs(slot, genesisTimestampMs));

    public static (ulong slot, long slotOffset) GetBuildingSlotAndOffset(ulong slotTimestampMs, ulong genesisTimestampMs)
    {
        long slotTimeSinceGenesis = (long)slotTimestampMs - (long)genesisTimestampMs;
        if (slotTimeSinceGenesis < 0)
        {
            throw new ShutterSlotCalulationException($"Slot timestamp {slotTimestampMs}ms was greater than genesis timestamp {genesisTimestampMs}ms.");
        }

        ulong buildingSlot = (ulong)slotTimeSinceGenesis / (ulong)SlotLength.TotalMilliseconds;
        long offset = GetCurrentOffsetMs(buildingSlot, genesisTimestampMs, slotTimestampMs);

        return (buildingSlot, offset);
    }
}
