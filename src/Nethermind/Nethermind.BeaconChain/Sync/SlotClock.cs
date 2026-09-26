// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Spec;
using Nethermind.Core;

namespace Nethermind.BeaconChain.Sync;

/// <summary>Wall-clock slot source derived from the spec genesis time and slot duration.</summary>
/// <remarks>Times before genesis map to slot 0; all values are computed on demand from <see cref="ITimestamper"/>.</remarks>
public class SlotClock(BeaconChainSpec spec, ITimestamper timestamper)
{
    private readonly long _genesisMilliseconds = (long)spec.GenesisTime * 1000;
    private readonly long _slotMilliseconds = (long)spec.SecondsPerSlot * 1000;

    /// <summary>The current unix time in milliseconds.</summary>
    public long UnixMilliseconds => timestamper.UtcNowOffset.ToUnixTimeMilliseconds();

    public ulong CurrentSlot
    {
        get
        {
            long sinceGenesis = UnixMilliseconds - _genesisMilliseconds;
            return sinceGenesis <= 0 ? 0 : (ulong)(sinceGenesis / _slotMilliseconds);
        }
    }

    public ulong CurrentEpoch => spec.GetEpoch(CurrentSlot);

    /// <summary>Time elapsed since the start of the current slot; zero before genesis.</summary>
    public TimeSpan TimeIntoSlot
    {
        get
        {
            long sinceGenesis = UnixMilliseconds - _genesisMilliseconds;
            return sinceGenesis <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(sinceGenesis % _slotMilliseconds);
        }
    }

    /// <summary>Milliseconds until the next slot starts (until genesis when before genesis); always positive.</summary>
    public long MillisecondsToNextSlot
    {
        get
        {
            long sinceGenesis = UnixMilliseconds - _genesisMilliseconds;
            return sinceGenesis < 0 ? -sinceGenesis : _slotMilliseconds - sinceGenesis % _slotMilliseconds;
        }
    }

    /// <summary>The unix time in milliseconds at which <paramref name="slot"/> starts.</summary>
    public long SlotStartMilliseconds(ulong slot) => _genesisMilliseconds + (long)slot * _slotMilliseconds;

    /// <summary>Yields the current slot at the start of every slot.</summary>
    /// <remarks>
    /// The first value is the next slot boundary observed after enumeration starts (slot 0 when
    /// starting before genesis). Slots whose boundary passes while the consumer is still processing
    /// the previous tick are skipped, so the yielded value is always the slot in progress.
    /// </remarks>
    public async IAsyncEnumerable<ulong> SlotTicks([EnumeratorCancellation] CancellationToken token = default)
    {
        while (true)
        {
            long now = UnixMilliseconds;
            long nextSlotStart = now < _genesisMilliseconds ? _genesisMilliseconds : SlotStartMilliseconds(CurrentSlot + 1);
            // Loop because timers can fire marginally before the requested due time.
            while (now < nextSlotStart)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(nextSlotStart - now), token);
                now = UnixMilliseconds;
            }

            yield return CurrentSlot;
        }
    }
}
