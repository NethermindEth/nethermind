// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using NonBlocking;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Stats.Model;

[assembly: InternalsVisibleTo("Nethermind.Synchronization.Test")]

namespace Nethermind.Synchronization.Peers
{
    public class PeerInfo
    {
        public const int SleepThreshold = 2;

        // Indices 0..SingleContextCount-1 cover the five single-bit contexts (used for both available slots and weakness).
        // The trailing entries are composite contexts that participate in weakness tracking only.
        private const int SingleContextCount = 5;
        private const int TrackedContextCount = 6;

        private static readonly AllocationContexts[] _orderedContexts =
        [
            AllocationContexts.Headers,
            AllocationContexts.Bodies,
            AllocationContexts.Receipts,
            AllocationContexts.State,
            AllocationContexts.Snap,
            AllocationContexts.Blocks,
        ];

        private readonly AllocationAllowances _allocationAllowances;
        private SlotsArray _availableSlots;
        private WeaknessArray _weaknesses;
        private uint _sleepingContexts;

        public PeerInfo(ISyncPeer syncPeer, AllocationAllowances? allocationAllowances = null)
        {
            SyncPeer = syncPeer;
            _allocationAllowances = allocationAllowances ?? AllocationAllowances.Default;
            for (int i = 0; i < SingleContextCount; i++)
            {
                _availableSlots[i] = _allocationAllowances[_orderedContexts[i]];
            }
        }

        public NodeClientType PeerClientType => SyncPeer?.ClientType ?? NodeClientType.Unknown;

        public AllocationAllowances AvailableAllocationSlots => new(
            (byte)Math.Max(0, Volatile.Read(ref _availableSlots[0])),
            (byte)Math.Max(0, Volatile.Read(ref _availableSlots[1])),
            (byte)Math.Max(0, Volatile.Read(ref _availableSlots[2])),
            (byte)Math.Max(0, Volatile.Read(ref _availableSlots[3])),
            (byte)Math.Max(0, Volatile.Read(ref _availableSlots[4])));

        public AllocationContexts SleepingContexts => (AllocationContexts)Volatile.Read(ref _sleepingContexts);

        private ConcurrentDictionary<AllocationContexts, DateTime?> SleepingSince { get; } = new();

        public ISyncPeer SyncPeer { get; }

        public bool IsInitialized => SyncPeer.IsInitialized;

        public UInt256 TotalDifficulty => SyncPeer.TotalDifficulty;

        public long HeadNumber => SyncPeer.HeadNumber;

        public Hash256 HeadHash => SyncPeer.HeadHash;

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
                if ((contexts & ctx) == ctx)
                    Interlocked.Increment(ref _availableSlots[i]);
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

        private static string BuildContextString(AllocationContexts contexts)
        {
            return $"{((contexts & AllocationContexts.Headers) == AllocationContexts.Headers ? "H" : " ")}{((contexts & AllocationContexts.Bodies) == AllocationContexts.Bodies ? "B" : " ")}{((contexts & AllocationContexts.Receipts) == AllocationContexts.Receipts ? "R" : " ")}{((contexts & AllocationContexts.State) == AllocationContexts.State ? "N" : " ")}{((contexts & AllocationContexts.Snap) == AllocationContexts.Snap ? "S" : " ")}";
        }

        private static readonly char[] _slotChars = ['h', 'b', 'r', 'n', 's'];

        private string BuildSlotContextString()
        {
            StringBuilder sb = new();
            for (int i = 0; i < SingleContextCount; i++)
            {
                int available = Math.Max(0, Volatile.Read(ref _availableSlots[i]));
                byte allowance = _allocationAllowances[_orderedContexts[i]];
                char ch = _slotChars[i];
                if (available == allowance)
                {
                    sb.Append(' ');
                }
                else if (available == 0)
                {
                    sb.Append(char.ToUpper(ch));
                }
                else if (allowance - available == 1)
                {
                    sb.Append(ch);
                }
                else
                {
                    sb.Append(allowance - available);
                }
            }
            return sb.ToString();
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
