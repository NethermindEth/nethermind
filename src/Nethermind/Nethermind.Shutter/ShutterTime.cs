// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Specs;

namespace Nethermind.Shutter;

public class ShutterTime(ISpecProvider specProvider, ITimestamper timestamper, TimeSpan slotLength, TimeSpan blockUpToDateCutoff)
{
    public class ShutterSlotCalulationException(string message, Exception? innerException = null) : Exception(message, innerException);
    public readonly ulong GenesisTimestamp = specProvider.ChainId == BlockchainIds.Chiado ? ChiadoSpecProvider.BeaconChainGenesisTimestamp : GnosisSpecProvider.BeaconChainGenesisTimestamp;

    public ulong GetGenesisTimestampMs() => 1000 * GenesisTimestamp;

    public ulong GetSlotTimestampMs(ulong slot, ulong genesisTimestampMs)
        => genesisTimestampMs + (ulong)slotLength.TotalMilliseconds * slot;

    public long GetCurrentOffsetMs(ulong slot, ulong genesisTimestampMs, ulong? slotTimestampMs = null)
        => timestamper.UtcNowOffset.ToUnixTimeMilliseconds() - (long)(slotTimestampMs ?? GetSlotTimestampMs(slot, genesisTimestampMs));

    public bool IsBlockUpToDate(Block head)
        => (ulong)timestamper.UtcNowOffset.ToUnixTimeSeconds() - head.Header.Timestamp < blockUpToDateCutoff.TotalSeconds;

    public ulong GetSlot(ulong slotTimestampMs, ulong genesisTimestampMs)
    {
        long slotTimeSinceGenesis = (long)slotTimestampMs - (long)genesisTimestampMs;
        if (slotTimeSinceGenesis < 0)
        {
            throw new ShutterSlotCalulationException($"Slot timestamp {slotTimestampMs}ms was before than genesis timestamp {genesisTimestampMs}ms.");
        }

        return (ulong)slotTimeSinceGenesis / (ulong)slotLength.TotalMilliseconds;
    }

    public (ulong slot, long slotOffset) GetBuildingSlotAndOffset(ulong slotTimestampMs, ulong genesisTimestampMs)
    {
        ulong buildingSlot = GetSlot(slotTimestampMs, genesisTimestampMs);
        long offset = GetCurrentOffsetMs(buildingSlot, genesisTimestampMs, slotTimestampMs);

        return (buildingSlot, offset);
    }
}
