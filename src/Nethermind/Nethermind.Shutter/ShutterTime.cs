// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Shutter;

public class ShutterTime(ulong genesisTimestampMs, ITimestamper timestamper, TimeSpan slotLength, TimeSpan blockUpToDateCutoff)
{
    public class ShutterSlotCalulationException(string message, Exception? innerException = null) : Exception(message, innerException);

    public readonly ulong GenesisTimestampMs = genesisTimestampMs;

    // n.b. cannot handle changes to slot length
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
            throw new ShutterSlotCalulationException($"Slot timestamp {slotTimestampMs}ms was before than genesis timestamp {GenesisTimestampMs}ms.");
        }

        return (ulong)slotTimeSinceGenesis / (ulong)slotLength.TotalMilliseconds;
    }
}
