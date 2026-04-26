// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.ComponentModel;
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
        private SlotsArray _availableSlots = InitialSlots(allocationAllowances ?? AllocationAllowances.Default);
        private WeaknessArray _weaknesses;
        private uint _sleepingContexts;

        private long _lastNotifiedEarliestNumber;
        private long _lastNotifiedLatestNumber;

        public NodeClientType PeerClientType => SyncPeer?.ClientType ?? NodeClientType.Unknown;

        public AllocationContexts AllocatedContexts
        {
            get
            {
                AllocationContexts result = AllocationContexts.None;
                for (int i = 0; i < SingleContextCount; i++)
                {
                    AllocationContexts ctx = _orderedContexts[i];
                    if (Volatile.Read(ref _availableSlots[i]) < _allocationAllowances[ctx])
                        result |= ctx;
                }
                return result;
            }
        }

        public AllocationAllowances AvailableAllocationSlots => new(
            (byte)Math.Max(0, Volatile.Read(ref _availableSlots[0])),
            (byte)Math.Max(0, Volatile.Read(ref _availableSlots[1])),
            (byte)Math.Max(0, Volatile.Read(ref _availableSlots[2])),
            (byte)Math.Max(0, Volatile.Read(ref _availableSlots[3])),
            (byte)Math.Max(0, Volatile.Read(ref _availableSlots[4])),
            (byte)Math.Max(0, Volatile.Read(ref _availableSlots[5])));

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

        public bool HasAnyAllocation
        {
            get
            {
                for (int i = 0; i < SingleContextCount; i++)
                {
                    if (Volatile.Read(ref _availableSlots[i]) < _allocationAllowances[_orderedContexts[i]])
                        return true;
                }
                return false;
            }
        }

        public bool CanBeAllocated(AllocationContexts contexts) =>
            !IsAsleep(contexts) && !IsAllocationFull(contexts) && this.SupportsAllocation(contexts);

        public bool IsAsleep(AllocationContexts contexts) =>
            ((AllocationContexts)Volatile.Read(ref _sleepingContexts) & contexts) != AllocationContexts.None;

        /// <summary>
        /// True if any single-bit context contained in <paramref name="contexts"/> currently has at least one slot taken.
        /// </summary>
        public bool IsAllocated(AllocationContexts contexts)
        {
            for (int i = 0; i < SingleContextCount; i++)
            {
                AllocationContexts ctx = _orderedContexts[i];
                if ((contexts & ctx) == ctx && Volatile.Read(ref _availableSlots[i]) < _allocationAllowances[ctx])
                    return true;
            }
            return false;
        }

        /// <summary>
        /// True if any single-bit context contained in <paramref name="contexts"/> has no remaining slots.
        /// </summary>
        public bool IsAllocationFull(AllocationContexts contexts)
        {
            for (int i = 0; i < SingleContextCount; i++)
            {
                AllocationContexts ctx = _orderedContexts[i];
                if ((contexts & ctx) == ctx && Volatile.Read(ref _availableSlots[i]) <= 0)
                    return true;
            }
            return false;
        }

        public bool TryAllocate(AllocationContexts contexts)
        {
            if (IsAsleep(contexts) || !this.SupportsAllocation(contexts)) return false;

            AllocationContexts allocated = AllocationContexts.None;
            for (int i = 0; i < SingleContextCount; i++)
            {
                AllocationContexts ctx = _orderedContexts[i];
                if ((contexts & ctx) != ctx) continue;

                if (!TryDecrement(i))
                {
                    Free(allocated);
                    return false;
                }
                allocated |= ctx;
            }
            return true;
        }

        public void Free(AllocationContexts contexts)
        {
            for (int i = 0; i < SingleContextCount; i++)
            {
                AllocationContexts ctx = _orderedContexts[i];
                if ((contexts & ctx) != ctx) continue;
                TryIncrementBoundedTo(i, _allocationAllowances[ctx]);
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

            for (int i = 0; i < TrackedContextCount; i++)
            {
                AllocationContexts ctx = _orderedContexts[i];
                if ((requested & ctx) == ctx)
                    Volatile.Write(ref _weaknesses[i], 0);
            }

            SleepingSince.TryRemove(requested, out _);
        }

        public AllocationContexts IncreaseWeakness(AllocationContexts requested)
        {
            AllocationContexts sleeps = AllocationContexts.None;
            for (int i = 0; i < TrackedContextCount; i++)
            {
                AllocationContexts ctx = _orderedContexts[i];
                if ((requested & ctx) == ctx &&
                    Interlocked.Increment(ref _weaknesses[i]) >= SleepThreshold)
                {
                    sleeps |= ctx;
                }
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryDecrement(int slotIndex)
        {
            ref int slot = ref _availableSlots[slotIndex];
            int current = Volatile.Read(ref slot);
            while (current > 0)
            {
                int observed = Interlocked.CompareExchange(ref slot, current - 1, current);
                if (observed == current) return true;
                current = observed;
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void TryIncrementBoundedTo(int slotIndex, byte ceiling)
        {
            // Skip Free of unallocated contexts: ceiling clamps the slot at its allowance.
            ref int slot = ref _availableSlots[slotIndex];
            int current = Volatile.Read(ref slot);
            while (current < ceiling)
            {
                int observed = Interlocked.CompareExchange(ref slot, current + 1, current);
                if (observed == current) return;
                current = observed;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SlotsArray InitialSlots(AllocationAllowances allowances)
        {
            SlotsArray slots = default;
            slots[0] = allowances.Headers;
            slots[1] = allowances.Bodies;
            slots[2] = allowances.Receipts;
            slots[3] = allowances.State;
            slots[4] = allowances.Snap;
            slots[5] = allowances.ForwardHeader;
            return slots;
        }

        private static string BuildContextString(AllocationContexts contexts) =>
            $"{((contexts & AllocationContexts.Headers) == AllocationContexts.Headers ? "H" : " ")}{((contexts & AllocationContexts.Bodies) == AllocationContexts.Bodies ? "B" : " ")}{((contexts & AllocationContexts.Receipts) == AllocationContexts.Receipts ? "R" : " ")}{((contexts & AllocationContexts.State) == AllocationContexts.State ? "N" : " ")}{((contexts & AllocationContexts.Snap) == AllocationContexts.Snap ? "S" : " ")}{((contexts & AllocationContexts.ForwardHeader) == AllocationContexts.ForwardHeader ? "F" : " ")}";

        // Letters track each single-bit context in the same order as _orderedContexts (Headers, Bodies, Receipts, State, Snap, ForwardHeader).
        private static ReadOnlySpan<char> SlotChars => ['h', 'b', 'r', 'n', 's', 'f'];

        private string BuildSlotContextString()
        {
            // Renders one character per slot:
            //  ' '          – all slots free (available == allowance)
            //  uppercase    – fully taken (available == 0)
            //  lowercase    – one slot taken (allowance - available == 1)
            //  digit        – multiple slots taken (allowance - available)
            Span<char> buf = stackalloc char[SingleContextCount];
            for (int i = 0; i < SingleContextCount; i++)
            {
                AllocationContexts ctx = _orderedContexts[i];
                int available = Math.Max(0, Volatile.Read(ref _availableSlots[i]));
                int allowance = _allocationAllowances[ctx];
                char ch = SlotChars[i];
                int taken = allowance - available;
                buf[i] = taken switch
                {
                    <= 0 => ' ',
                    1 when allowance > 1 => ch,
                    _ when available == 0 => char.ToUpper(ch),
                    _ => (char)('0' + Math.Min(taken, 9)),
                };
            }
            return new string(buf);
        }

        public override string ToString() => $"[{BuildSlotContextString()} ][{BuildContextString(SleepingContexts)} ]{SyncPeer}";

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsOnlyOneContext(AllocationContexts x) => (x & (x - 1)) == 0;

        [InlineArray(SingleContextCount)]
        private struct SlotsArray
        {
            private int _slot0;
        }

        [InlineArray(TrackedContextCount)]
        private struct WeaknessArray
        {
            private int _slot0;
        }
    }
}
