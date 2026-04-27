// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using NonBlocking;
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

        // Indices 0..SingleContextCount-1 cover the six single-bit contexts (used for both available slots and weakness).
        // The trailing entry is the composite Blocks flag, which participates in weakness tracking only.
        private const int SingleContextCount = 6;
        private const int TrackedContextCount = 7;

        private const int SlotBits = 8; // 8 bits per slot in the available-slot word
        private const ulong SlotByteMask = 0xFFul;
        private const int WeaknessBits = 4; // 4 bits per weakness counter (range 0..15, threshold 2)
        private const uint WeaknessNibbleMask = 0xFu;

        private static readonly AllocationContexts[] _orderedContexts =
        [
            AllocationContexts.Headers,
            AllocationContexts.Bodies,
            AllocationContexts.Receipts,
            AllocationContexts.State,
            AllocationContexts.Snap,
            AllocationContexts.ForwardHeader,
            AllocationContexts.Blocks,
        ];

        private readonly AllocationAllowances _allocationAllowances = allocationAllowances ?? AllocationAllowances.Default;
        private readonly ulong _maxPacked = Pack(allocationAllowances ?? AllocationAllowances.Default);
        private ulong _availableSlots = Pack(allocationAllowances ?? AllocationAllowances.Default);
        private uint _weaknesses;
        private uint _sleepingContexts;

        private long _lastNotifiedEarliestNumber;
        private long _lastNotifiedLatestNumber;

        public NodeClientType PeerClientType => SyncPeer?.ClientType ?? NodeClientType.Unknown;

        public ISyncPeer SyncPeer { get; } = syncPeer;

        public bool IsInitialized => SyncPeer.IsInitialized;

        /// <summary>See <see cref="ISyncPeer.TotalDifficulty"/>.</summary>
        public UInt256? TotalDifficulty => SyncPeer.TotalDifficulty;

        public long HeadNumber => SyncPeer.HeadNumber;
        public Hash256 HeadHash => SyncPeer.HeadHash;

        public AllocationContexts SleepingContexts => (AllocationContexts)Volatile.Read(ref _sleepingContexts);

        private ConcurrentDictionary<AllocationContexts, DateTime?> SleepingSince { get; } = new();

        public AllocationContexts AllocatedContexts
        {
            get
            {
                ulong available = ReadSlots();
                AllocationContexts result = AllocationContexts.None;
                for (int i = 0; i < SingleContextCount; i++)
                {
                    if (((available >> (i * SlotBits)) & SlotByteMask) < ((_maxPacked >> (i * SlotBits)) & SlotByteMask))
                        result |= _orderedContexts[i];
                }
                return result;
            }
        }

        public AllocationAllowances AvailableAllocationSlots
        {
            get
            {
                // Loop-driven via the AllocationAllowances indexer so this stays in sync with _orderedContexts;
                // the positional ctor is the one place that hard-codes the field-to-byte mapping.
                ulong a = ReadSlots();
                AllocationAllowances result = default;
                for (int i = 0; i < SingleContextCount; i++)
                {
                    result[_orderedContexts[i]] = (byte)(a >> (i * SlotBits));
                }
                return result;
            }
        }

        public bool CanBeAllocated(AllocationContexts contexts) =>
            !IsAsleep(contexts) && !IsAllocationFull(contexts) && this.SupportsAllocation(contexts);

        public bool IsAsleep(AllocationContexts contexts) =>
            ((AllocationContexts)Volatile.Read(ref _sleepingContexts) & contexts) != AllocationContexts.None;

        /// <summary>True if any single-bit context in <paramref name="contexts"/> currently has at least one slot taken.</summary>
        public bool IsAllocated(AllocationContexts contexts)
        {
            ulong available = ReadSlots();
            for (int i = 0; i < SingleContextCount; i++)
            {
                AllocationContexts ctx = _orderedContexts[i];
                if ((contexts & ctx) == ctx)
                {
                    int shift = i * SlotBits;
                    if (((available >> shift) & SlotByteMask) < ((_maxPacked >> shift) & SlotByteMask))
                        return true;
                }
            }
            return false;
        }

        /// <summary>True if any single-bit context in <paramref name="contexts"/> has no remaining slots.</summary>
        public bool IsAllocationFull(AllocationContexts contexts)
        {
            ulong available = ReadSlots();
            for (int i = 0; i < SingleContextCount; i++)
            {
                AllocationContexts ctx = _orderedContexts[i];
                if ((contexts & ctx) == ctx)
                {
                    if (((available >> (i * SlotBits)) & SlotByteMask) == 0)
                        return true;
                }
            }
            return false;
        }

        public bool TryAllocate(AllocationContexts contexts)
        {
            if (IsAsleep(contexts) || !this.SupportsAllocation(contexts)) return false;

            // Vacuous success when the request carries no single-bit contexts (e.g. AllocationContexts.None);
            // matches the bitmask-era behaviour callers depend on.
            ulong delta = BuildSlotDelta(contexts);
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

        public void Free(AllocationContexts contexts) => IncrementSlotsBounded(BuildSlotDelta(contexts));

        public void PutToSleep(AllocationContexts contexts, DateTime dateTime)
        {
            Interlocked.Or(ref _sleepingContexts, (uint)contexts);
            SleepingSince[contexts] = dateTime;
        }

        public void TryToWakeUp(DateTime dateTime, TimeSpan wakeUpIfSleepsMoreThanThis)
        {
            foreach (KeyValuePair<AllocationContexts, DateTime?> keyValuePair in SleepingSince)
            {
                if (IsAsleep(keyValuePair.Key) && dateTime - keyValuePair.Value >= wakeUpIfSleepsMoreThanThis)
                {
                    WakeUp(keyValuePair.Key);
                }
            }
        }

        private void WakeUp(AllocationContexts requested)
        {
            Interlocked.And(ref _sleepingContexts, ~(uint)requested);

            uint clearMask = 0;
            for (int i = 0; i < TrackedContextCount; i++)
            {
                AllocationContexts ctx = _orderedContexts[i];
                if ((requested & ctx) == ctx)
                    clearMask |= WeaknessNibbleMask << (i * WeaknessBits);
            }
            if (clearMask != 0) Interlocked.And(ref _weaknesses, ~clearMask);

            SleepingSince.TryRemove(requested, out _);
        }

        public AllocationContexts IncreaseWeakness(AllocationContexts requested)
        {
            uint delta = BuildWeaknessDelta(requested);
            if (delta == 0) return AllocationContexts.None;

            uint updated = AddWeaknessSaturating(delta);

            AllocationContexts sleeps = AllocationContexts.None;
            for (int i = 0; i < TrackedContextCount; i++)
            {
                AllocationContexts ctx = _orderedContexts[i];
                if ((requested & ctx) != ctx) continue;
                if (((updated >> (i * WeaknessBits)) & WeaknessNibbleMask) >= SleepThreshold)
                    sleeps |= ctx;
            }
            return sleeps;
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
        public bool ShouldNotifyNewRange(long earliestNumber, long latestNumber)
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
        /// Build a 1-per-participating-slot delta for the available-slots word from <paramref name="contexts"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong BuildSlotDelta(AllocationContexts contexts)
        {
            ulong delta = 0;
            for (int i = 0; i < SingleContextCount; i++)
            {
                AllocationContexts ctx = _orderedContexts[i];
                if ((contexts & ctx) == ctx) delta |= 1ul << (i * SlotBits);
            }
            return delta;
        }

        /// <summary>
        /// Atomically subtract <paramref name="delta"/> from the packed slot word; fail if any participating slot is zero.
        /// </summary>
        private bool TryDecrementSlots(ulong delta)
        {
            ulong old = ReadSlots();
            while (true)
            {
                if (HasZeroSlot(old, delta)) return false;
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
                ulong increments = delta & ~CarryOnSaturation(old, delta, _maxPacked);
                if (increments == 0) return;
                ulong updated = old + increments;
                ulong observed = Interlocked.CompareExchange(ref _availableSlots, updated, old);
                if (observed == old) return;
                old = observed;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint BuildWeaknessDelta(AllocationContexts contexts)
        {
            uint delta = 0;
            for (int i = 0; i < TrackedContextCount; i++)
            {
                AllocationContexts ctx = _orderedContexts[i];
                if ((contexts & ctx) == ctx) delta |= 1u << (i * WeaknessBits);
            }
            return delta;
        }

        private uint AddWeaknessSaturating(uint delta)
        {
            uint old = Volatile.Read(ref _weaknesses);
            while (true)
            {
                uint updated = SaturatingAdd(old, delta);
                uint observed = Interlocked.CompareExchange(ref _weaknesses, updated, old);
                if (observed == old) return updated;
                old = observed;
            }
        }

        // Drives byte-slot positions from _orderedContexts so Pack, BuildSlotDelta, and the byte readers in
        // IsAllocated / IsAllocationFull / AvailableAllocationSlots all share one ordering source of truth.
        private static ulong Pack(AllocationAllowances a)
        {
            ulong packed = 0;
            for (int i = 0; i < SingleContextCount; i++)
                packed |= ((ulong)a[_orderedContexts[i]]) << (i * SlotBits);
            return packed;
        }

        /// <summary>
        /// True if any byte-slot of <paramref name="value"/> is zero among the byte boundaries set in <paramref name="participating"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool HasZeroSlot(ulong value, ulong participating)
        {
            for (ulong remaining = participating; remaining != 0;)
            {
                int shift = BitOperations.TrailingZeroCount(remaining) & ~7;
                if (((value >> shift) & SlotByteMask) == 0) return true;
                remaining &= ~(SlotByteMask << shift);
            }
            return false;
        }

        /// <summary>
        /// For each participating slot, returns its low bit if the slot is already at <paramref name="cap"/>'s value.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong CarryOnSaturation(ulong value, ulong delta, ulong cap)
        {
            ulong saturated = 0;
            for (ulong remaining = delta; remaining != 0;)
            {
                int bit = BitOperations.TrailingZeroCount(remaining);
                int shift = bit & ~7;
                if (((value >> shift) & SlotByteMask) >= ((cap >> shift) & SlotByteMask))
                    saturated |= 1ul << bit;
                remaining &= ~(SlotByteMask << shift);
            }
            return saturated;
        }

        /// <summary>
        /// Adds <paramref name="delta"/> to <paramref name="value"/> nibble-wise, clamping each nibble at <see cref="WeaknessNibbleMask"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint SaturatingAdd(uint value, uint delta)
        {
            uint result = value;
            for (uint remaining = delta; remaining != 0;)
            {
                int shift = BitOperations.TrailingZeroCount(remaining) & ~3;
                uint cur = (result >> shift) & WeaknessNibbleMask;
                uint inc = (delta >> shift) & WeaknessNibbleMask;
                uint sum = Math.Min(cur + inc, WeaknessNibbleMask);
                result = (result & ~(WeaknessNibbleMask << shift)) | (sum << shift);
                remaining &= ~(WeaknessNibbleMask << shift);
            }
            return result;
        }

        // Per-single-bit-context glyphs in the order of _orderedContexts.
        private static ReadOnlySpan<char> ContextChars => ['H', 'B', 'R', 'N', 'S', 'F'];
        private static ReadOnlySpan<char> SlotChars => ['h', 'b', 'r', 'n', 's', 'f'];

        private static string BuildContextString(AllocationContexts contexts)
        {
            Span<char> buf = stackalloc char[SingleContextCount];
            for (int i = 0; i < SingleContextCount; i++)
            {
                AllocationContexts ctx = _orderedContexts[i];
                buf[i] = (contexts & ctx) == ctx ? ContextChars[i] : ' ';
            }
            return new string(buf);
        }

        private string BuildSlotContextString()
        {
            ulong available = ReadSlots();
            Span<char> buf = stackalloc char[SingleContextCount];
            for (int i = 0; i < SingleContextCount; i++)
            {
                int avail = (int)((available >> (i * SlotBits)) & SlotByteMask);
                int allowance = _allocationAllowances[_orderedContexts[i]];
                char ch = SlotChars[i];
                int taken = allowance - avail;
                buf[i] = taken switch
                {
                    <= 0 => ' ',
                    1 when allowance > 1 => ch,
                    _ when avail == 0 => char.ToUpper(ch),
                    _ => (char)('0' + Math.Min(taken, 9)),
                };
            }
            return new string(buf);
        }

        public override string ToString() => $"[{BuildSlotContextString()} ][{BuildContextString(SleepingContexts)} ]{SyncPeer}";
    }
}
