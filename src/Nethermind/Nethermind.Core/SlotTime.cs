// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core;

public class SlotTime(ulong genesisTimestampMs, TimeSpan slotLength)
{
    public class SlotCalculationException(string message, Exception? innerException = null) : Exception(message, innerException);

    public readonly ulong GenesisTimestampMs = genesisTimestampMs;

    public ulong GetSlot(ulong slotTimestampMs)
    {
        long slotTimeSinceGenesis = (long)slotTimestampMs - (long)GenesisTimestampMs;
        if (slotTimeSinceGenesis < 0)
        {
            throw new SlotCalculationException($"Slot timestamp {slotTimestampMs}ms was before than genesis timestamp {GenesisTimestampMs}ms.");
        }

        return (ulong)slotTimeSinceGenesis / (ulong)slotLength.TotalMilliseconds;
    }
}
