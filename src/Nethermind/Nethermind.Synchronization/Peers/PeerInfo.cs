// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Stats.Model;

[assembly: InternalsVisibleTo("Nethermind.Synchronization.Test")]

namespace Nethermind.Synchronization.Peers
{
    public class PeerInfo(ISyncPeer syncPeer, AllocationAllowances? allocationAllowances = null)
    {
        public const int SleepThreshold = 2;

        private readonly AllocationAllowances _allocationAllowances = allocationAllowances ?? AllocationAllowances.Default;
        private readonly ulong _maxPacked = (allocationAllowances ?? AllocationAllowances.Default).Pack();
        private ulong _availableSlots = (allocationAllowances ?? AllocationAllowances.Default).Pack();
        private uint _weaknesses;
        private uint _sleepingContexts;

        private ulong _lastNotifiedEarliestNumber;
        private ulong _lastNotifiedLatestNumber;

        public NodeClientType PeerClientType => SyncPeer?.ClientType ?? NodeClientType.Unknown;

        public ISyncPeer SyncPeer { get; } = syncPeer;

        public bool IsInitialized => SyncPeer.IsInitialized;

        /// <summary>See <see cref="ISyncPeer.TotalDifficulty"/>.</summary>
        public UInt256? TotalDifficulty => SyncPeer.TotalDifficulty;

        public ulong HeadNumber => SyncPeer.HeadNumber;
        public Hash256 HeadHash => SyncPeer.HeadHash;

        public AllocationContexts SleepingContexts => (AllocationContexts)Volatile.Read(ref _sleepingContexts);

        private long _sleepGeneration;

        private ConcurrentDictionary<AllocationContexts, SleepEntry> SleepingSince { get; } = new();

        /// <summary>
        /// One sleep of a context set: a unique generation plus the time it was put to sleep.
        /// </summary>
        /// <remarks>
        /// The generation makes every sleep distinguishable even when timestamps collide, so
        /// <see cref="WakeUp"/> can remove exactly the sleep it observed and never a concurrent refresh.
        /// </remarks>
        private readonly record struct SleepEntry(long Generation, DateTime Since);

        public AllocationContexts AllocatedContexts =>
            AllocationAllowances.AllocatedFrom(ReadSlots(), _maxPacked);

        public AllocationAllowances AvailableAllocationSlots =>
            AllocationAllowances.Unpack(ReadSlots());

        public bool CanBeAllocated(AllocationContexts contexts) =>
            !IsAsleep(contexts) && !IsAllocationFull(contexts) && this.SupportsAllocation(contexts);

        public bool IsAsleep(AllocationContexts contexts) =>
            ((AllocationContexts)Volatile.Read(ref _sleepingContexts) & contexts) != AllocationContexts.None;

        /// <summary>True if any single-bit context in <paramref name="contexts"/> currently has at least one slot taken.</summary>
        public bool IsAllocated(AllocationContexts contexts) =>
            AllocationAllowances.AnyAllocated(ReadSlots(), _maxPacked, contexts);

        /// <summary>True if any single-bit context in <paramref name="contexts"/> has no remaining slots.</summary>
        public bool IsAllocationFull(AllocationContexts contexts) =>
            AllocationAllowances.AnyFull(ReadSlots(), contexts);

        public bool TryAllocate(AllocationContexts contexts)
        {
            if (IsAsleep(contexts) || !this.SupportsAllocation(contexts)) return false;

            // Vacuous success when the request carries no single-bit contexts (e.g. AllocationContexts.None);
            // matches the bitmask-era behaviour callers depend on.
            ulong delta = AllocationAllowances.BuildSlotDelta(contexts);
            if (delta == 0) return true;
            if (!TryDecrementSlots(delta)) return false;

            // Lock-free TOCTOU close-out: the IsAsleep check above and the CAS are not atomic, so a concurrent
            // PutToSleep can fire after we've taken the slots. Re-check and roll back so a sleeping peer never
            // holds an active allocation slot once the sleeping flag is observable.
            if (IsAsleep(contexts))
            {
                Free(contexts);
                return false;
            }
            return true;
        }

        public void Free(AllocationContexts contexts) =>
            IncrementSlotsBounded(AllocationAllowances.BuildSlotDelta(contexts));

        public void PutToSleep(AllocationContexts contexts, DateTime dateTime)
        {
            Interlocked.Or(ref _sleepingContexts, (uint)contexts);
            SleepingSince[contexts] = new SleepEntry(Interlocked.Increment(ref _sleepGeneration), dateTime);
        }

        public void TryToWakeUp(DateTime dateTime, TimeSpan wakeUpIfSleepsMoreThanThis)
        {
            foreach (KeyValuePair<AllocationContexts, SleepEntry> keyValuePair in SleepingSince)
            {
                if (IsAsleep(keyValuePair.Key) && dateTime - keyValuePair.Value.Since >= wakeUpIfSleepsMoreThanThis)
                {
                    WakeUp(keyValuePair);
                }
            }
        }

        /// <summary>
        /// Wakes the contexts of one observed <see cref="SleepingSince"/> entry.
        /// </summary>
        /// <remarks>
        /// The entry is removed first, conditioned on the exact observed generation, so a concurrent
        /// <see cref="PutToSleep"/> that refreshed the entry wins and the peer stays asleep until a
        /// later wake-up pass. Removing unconditionally (or clearing the sleeping bits before the
        /// removal outcome is known) could delete a refreshed entry while its sleeping bits survive,
        /// leaving the peer asleep with nothing for <see cref="TryToWakeUp"/> to enumerate —
        /// permanently unallocatable for those contexts. <see cref="PutToSleep"/> writes the entry
        /// after setting the bits for the same reason: whenever the bits are observable, an entry
        /// covering them is either already present or about to be written with a fresh generation.
        /// </remarks>
        private void WakeUp(KeyValuePair<AllocationContexts, SleepEntry> sleep)
        {
            if (!SleepingSince.TryRemove(sleep)) return;

            Interlocked.And(ref _sleepingContexts, ~(uint)sleep.Key);

            uint clearMask = WeaknessTracking.ClearMaskFor(sleep.Key);
            if (clearMask != 0) Interlocked.And(ref _weaknesses, ~clearMask);
        }

        public AllocationContexts IncreaseWeakness(AllocationContexts requested)
        {
            uint delta = WeaknessTracking.BuildDelta(requested);
            if (delta == 0) return AllocationContexts.None;

            uint updated = AddWeaknessSaturating(delta);
            return WeaknessTracking.AtOrAboveThreshold(updated, requested, SleepThreshold);
        }

        public bool HasEqualOrBetterTDOrBlock(PeerInfo? other)
        {
            if (other is null) return true;
            if (TotalDifficulty is null || other.TotalDifficulty is null) return HeadNumber >= other.HeadNumber;
            return TotalDifficulty >= other.TotalDifficulty;
        }

        public void EnsureInitialized()
        {
            if (!IsInitialized)
                throw new InvalidAsynchronousStateException($"{GetType().Name} found an uninitialized peer - {this}");
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool ShouldNotifyNewRange(ulong earliestNumber, ulong latestNumber)
        {
            // Also notify if same header as could be reorg with different hash
            if (latestNumber < _lastNotifiedLatestNumber && earliestNumber < _lastNotifiedEarliestNumber)
                return false;
            _lastNotifiedEarliestNumber = earliestNumber;
            _lastNotifiedLatestNumber = latestNumber;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ulong ReadSlots() => Volatile.Read(ref _availableSlots);

        /// <summary>
        /// Atomically subtract <paramref name="delta"/> from the packed slot word; fail if any participating slot is zero.
        /// </summary>
        private bool TryDecrementSlots(ulong delta)
        {
            ulong old = ReadSlots();
            while (true)
            {
                if (AllocationAllowances.HasZeroSlot(old, delta)) return false;
                ulong updated = old - delta;
                ulong observed = Interlocked.CompareExchange(ref _availableSlots, updated, old);
                if (observed == old) return true;
                old = observed;
            }
        }

        /// <summary>
        /// Atomically add <paramref name="delta"/> to the packed slot word, saturating each slot at its allowance ceiling.
        /// </summary>
        private void IncrementSlotsBounded(ulong delta)
        {
            if (delta == 0) return;
            ulong old = ReadSlots();
            while (true)
            {
                ulong increments = delta & ~AllocationAllowances.CarryOnSaturation(old, delta, _maxPacked);
                if (increments == 0) return;
                ulong updated = old + increments;
                ulong observed = Interlocked.CompareExchange(ref _availableSlots, updated, old);
                if (observed == old) return;
                old = observed;
            }
        }

        private uint AddWeaknessSaturating(uint delta)
        {
            uint old = Volatile.Read(ref _weaknesses);
            while (true)
            {
                uint updated = WeaknessTracking.SaturatingAdd(old, delta);
                uint observed = Interlocked.CompareExchange(ref _weaknesses, updated, old);
                if (observed == old) return updated;
                old = observed;
            }
        }

        // Per-single-bit-context glyphs in the order of AllocationAllowances.OrderedSingleContexts.
        private static ReadOnlySpan<char> ContextChars => ['H', 'B', 'R', 'N', 'S', 'F'];

        private static string BuildContextString(AllocationContexts contexts)
        {
            Span<char> buf = stackalloc char[AllocationAllowances.SingleContextCount];
            for (int i = 0; i < AllocationAllowances.SingleContextCount; i++)
            {
                AllocationContexts ctx = AllocationAllowances.OrderedSingleContexts[i];
                buf[i] = (contexts & ctx) == ctx ? ContextChars[i] : ' ';
            }
            return new string(buf);
        }

        private string BuildSlotContextString()
        {
            AllocationAllowances available = AllocationAllowances.Unpack(ReadSlots());
            Span<char> buf = stackalloc char[AllocationAllowances.SingleContextCount];
            for (int i = 0; i < AllocationAllowances.SingleContextCount; i++)
            {
                AllocationContexts ctx = AllocationAllowances.OrderedSingleContexts[i];
                int avail = available[ctx];
                int taken = _allocationAllowances[ctx] - avail;
                // Letter marks "in flight" — single take or saturated; digit shows multi-take partial fill.
                buf[i] = (taken, avail) switch
                {
                    ( <= 0, _) => ' ',
                    (1, _) or (_, 0) => ContextChars[i],
                    _ => (char)('0' + Math.Min(taken, 9)),
                };
            }
            return new string(buf);
        }

        public override string ToString() => $"[{BuildSlotContextString()} ][{BuildContextString(SleepingContexts)} ]{SyncPeer}";
    }
}
