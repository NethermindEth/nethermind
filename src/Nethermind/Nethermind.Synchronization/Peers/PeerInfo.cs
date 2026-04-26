// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
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

        private const int SlotBits = AllocationAllowances.BitsPerSlot;        // 8 bits per slot in the available-slot word
        private const ulong SlotByteMask = 0xFFul;
        private const int WeaknessBits = 4;                                   // 4 bits per weakness counter (range 0..15, threshold 2)
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
        private ulong _availableSlots = (allocationAllowances ?? AllocationAllowances.Default).Packed;
        private uint _weaknesses;
        private uint _sleepingContexts;

        private long _lastNotifiedEarliestNumber;
        private long _lastNotifiedLatestNumber;

        public NodeClientType PeerClientType => SyncPeer?.ClientType ?? NodeClientType.Unknown;

        public AllocationContexts AllocatedContexts
        {
            get
            {
                ulong available = Volatile.Read(ref _availableSlots);
                ulong max = _allocationAllowances.Packed;
                AllocationContexts result = AllocationContexts.None;
                for (int i = 0; i < SingleContextCount; i++)
                {
                    int shift = i * SlotBits;
                    if (((available >> shift) & SlotByteMask) < ((max >> shift) & SlotByteMask))
                        result |= _orderedContexts[i];
                }
                return result;
            }
        }

        public AllocationAllowances AvailableAllocationSlots => new() { Packed = Volatile.Read(ref _availableSlots) };

        public AllocationContexts SleepingContexts => (AllocationContexts)Volatile.Read(ref _sleepingContexts);

        private ConcurrentDictionary<AllocationContexts, DateTime?> SleepingSince { get; } = new();

        public ISyncPeer SyncPeer { get; } = syncPeer;

        public bool IsInitialized => SyncPeer.IsInitialized;

        /// <summary>
        /// See <see cref="ISyncPeer.TotalDifficulty"/>.
        /// </summary>
        public UInt256? TotalDifficulty => SyncPeer.TotalDifficulty;

        public long HeadNumber => SyncPeer.HeadNumber;

        public Hash256 HeadHash => SyncPeer.HeadHash;

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

        public bool HasAnyAllocation => Volatile.Read(ref _availableSlots) != _allocationAllowances.Packed;

        public bool CanBeAllocated(AllocationContexts contexts) =>
            !IsAsleep(contexts) && !IsAllocationFull(contexts) && this.SupportsAllocation(contexts);

        public bool IsAsleep(AllocationContexts contexts) =>
            ((AllocationContexts)Volatile.Read(ref _sleepingContexts) & contexts) != AllocationContexts.None;

        /// <summary>
        /// True if any single-bit context contained in <paramref name="contexts"/> currently has at least one slot taken.
        /// </summary>
        public bool IsAllocated(AllocationContexts contexts)
        {
            ulong available = Volatile.Read(ref _availableSlots);
            ulong max = _allocationAllowances.Packed;
            for (int i = 0; i < SingleContextCount; i++)
            {
                AllocationContexts ctx = _orderedContexts[i];
                if ((contexts & ctx) != ctx) continue;
                int shift = i * SlotBits;
                if (((available >> shift) & SlotByteMask) < ((max >> shift) & SlotByteMask)) return true;
            }
            return false;
        }

        /// <summary>
        /// True if any single-bit context contained in <paramref name="contexts"/> has no remaining slots.
        /// </summary>
        public bool IsAllocationFull(AllocationContexts contexts)
        {
            ulong available = Volatile.Read(ref _availableSlots);
            for (int i = 0; i < SingleContextCount; i++)
            {
                AllocationContexts ctx = _orderedContexts[i];
                if ((contexts & ctx) != ctx) continue;
                if (((available >> (i * SlotBits)) & SlotByteMask) == 0) return true;
            }
            return false;
        }

        public bool TryAllocate(AllocationContexts contexts)
        {
            if (IsAsleep(contexts) || !this.SupportsAllocation(contexts)) return false;

            // Build a "delta" mask: for each single-bit context in `contexts`, decrement the corresponding byte slot
            // atomically via a single CAS on the whole ulong, retrying if any slot is empty.
            ulong delta = 0;
            for (int i = 0; i < SingleContextCount; i++)
            {
                AllocationContexts ctx = _orderedContexts[i];
                if ((contexts & ctx) == ctx) delta |= 1ul << (i * SlotBits);
            }
            // Vacuous success when the request carries no single-bit contexts (e.g. AllocationContexts.None);
            // matches the bitmask-era behaviour callers depend on.
            if (delta == 0) return true;

            ulong old = Volatile.Read(ref _availableSlots);
            while (true)
            {
                if (HasZeroSlot(old, delta)) return false;
                ulong updated = old - delta;
                ulong observed = Interlocked.CompareExchange(ref _availableSlots, updated, old);
                if (observed == old) break;
                old = observed;
            }

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

        public void Free(AllocationContexts contexts)
        {
            ulong delta = 0;
            ulong cap = 0;
            for (int i = 0; i < SingleContextCount; i++)
            {
                AllocationContexts ctx = _orderedContexts[i];
                if ((contexts & ctx) != ctx) continue;
                int shift = i * SlotBits;
                delta |= 1ul << shift;
                cap |= ((ulong)_allocationAllowances[ctx]) << shift;
            }
            if (delta == 0) return;

            ulong old = Volatile.Read(ref _availableSlots);
            while (true)
            {
                // Mask off the slots that are already at their ceiling so spurious frees can't overflow past max.
                ulong increments = delta & ~CarryOnSaturation(old, delta, cap);
                if (increments == 0) return;
                ulong updated = old + increments;
                ulong observed = Interlocked.CompareExchange(ref _availableSlots, updated, old);
                if (observed == old) return;
                old = observed;
            }
        }

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

            // Clear weakness counters for every (single-bit + composite) context whose flags are fully set in `requested`.
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
            // Build the per-slot increment mask once, then CAS-add it to the packed weakness word.
            uint delta = 0;
            for (int i = 0; i < TrackedContextCount; i++)
            {
                AllocationContexts ctx = _orderedContexts[i];
                if ((requested & ctx) == ctx) delta |= 1u << (i * WeaknessBits);
            }
            if (delta == 0) return AllocationContexts.None;

            uint old = Volatile.Read(ref _weaknesses);
            uint updated;
            while (true)
            {
                updated = SaturatingAdd(old, delta);
                uint observed = Interlocked.CompareExchange(ref _weaknesses, updated, old);
                if (observed == old) break;
                old = observed;
            }

            // Anything that crossed the threshold becomes a sleep candidate.
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
            if (other is null)
                return true;

            if (TotalDifficulty is null || other.TotalDifficulty is null)
                return HeadNumber >= other.HeadNumber;

            return TotalDifficulty >= other.TotalDifficulty;
        }

        public void EnsureInitialized()
        {
            if (!IsInitialized)
            {
                throw new InvalidAsynchronousStateException($"{GetType().Name} found an uninitialized peer - {this}");
            }
        }

        /// <summary>
        /// Returns a mask of byte-slots in <paramref name="value"/> that are zero, restricted to slots set in <paramref name="participating"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool HasZeroSlot(ulong value, ulong participating)
        {
            // For each participating slot, check the byte is non-zero. We do this by ORing the byte into the slot's
            // low bit and seeing if the participating bit survives.
            for (ulong remaining = participating; remaining != 0;)
            {
                int bit = BitOperations.TrailingZeroCount(remaining);
                int slotShift = bit & ~7;          // align to byte boundary
                if (((value >> slotShift) & SlotByteMask) == 0) return true;
                remaining &= ~(SlotByteMask << slotShift); // clear the whole byte from the search mask
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
                ulong cur = (value >> shift) & SlotByteMask;
                ulong limit = (cap >> shift) & SlotByteMask;
                if (cur >= limit) saturated |= 1ul << bit;
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
                int bit = BitOperations.TrailingZeroCount(remaining);
                int shift = bit & ~3;
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
            ulong available = Volatile.Read(ref _availableSlots);
            Span<char> buf = stackalloc char[SingleContextCount];
            for (int i = 0; i < SingleContextCount; i++)
            {
                int shift = i * SlotBits;
                int avail = (int)((available >> shift) & SlotByteMask);
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsOnlyOneContext(AllocationContexts x) => (x & (x - 1)) == 0;
    }
}
