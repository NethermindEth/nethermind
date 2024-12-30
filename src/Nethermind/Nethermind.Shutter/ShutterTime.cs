// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Specs;

namespace Nethermind.Shutter;

public class ShutterTime(ulong genesisTimestampMs, ITimestamper timestamper, TimeSpan slotLength, TimeSpan blockUpToDateCutoff, ISpecProvider specProvider)
{
    public class ShutterSlotCalulationException(string message, Exception? innerException = null) : Exception(message, innerException);

    public readonly ulong GenesisTimestampMs = genesisTimestampMs;

    public ulong GetSlotTimestampMs(ulong slot)
        => GenesisTimestampMs + slot * (ulong)slotLength.TotalMilliseconds;

    public long GetCurrentOffsetMs(ulong slot, ulong? slotTimestampMs = null)
        => timestamper.UtcNowOffset.ToUnixTimeMilliseconds() - (long)(slotTimestampMs ?? GetSlotTimestampMs(slot));

    public bool IsBlockUpToDate(Block head)
        => timestamper.UtcNowOffset.ToUnixTimeSeconds() - (long)head.Header.Timestamp < blockUpToDateCutoff.TotalSeconds;

    public (ulong slot, long slotOffset) GetBuildingSlotAndOffset(ulong slotTimestampMs)
    {
        try
        {
            ulong buildingSlot = specProvider.CalculateSlot(slotTimestampMs / 1000);
            long offset = GetCurrentOffsetMs(buildingSlot, slotTimestampMs);
            return (buildingSlot, offset);
        }
        catch (InvalidOperationException ex)
        {
            throw new ShutterSlotCalulationException($"Unable to calculate slot for timestamp {slotTimestampMs / 1000}s.", ex);
        }
    }
}
