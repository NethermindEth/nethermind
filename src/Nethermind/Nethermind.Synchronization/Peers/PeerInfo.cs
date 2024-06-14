// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using NonBlocking;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using FastEnumUtility;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Stats.Model;

[assembly: InternalsVisibleTo("Nethermind.Synchronization.Test")]

namespace Nethermind.Synchronization.Peers
{
    public class PeerInfo
    {
        public static readonly FrozenDictionary<AllocationContexts, int> DefaultAllowances = new Dictionary<AllocationContexts, int>()
        {
            {AllocationContexts.Headers, 1},
            {AllocationContexts.Bodies, 1},
            {AllocationContexts.Receipts, 1},
            {AllocationContexts.State, 1},
            {AllocationContexts.Snap, 1},
        }.ToFrozenDictionary();

        private readonly FrozenDictionary<AllocationContexts, int> _allocationAllowances;

        public PeerInfo(ISyncPeer syncPeer, FrozenDictionary<AllocationContexts, int>? allocationAllowances = null)
        {
            SyncPeer = syncPeer;
            _allocationAllowances = allocationAllowances?.ToFrozenDictionary() ?? DefaultAllowances;
            AllocationSlots = new ConcurrentDictionary<AllocationContexts, int>(_allocationAllowances);
        }

        public NodeClientType PeerClientType => SyncPeer?.ClientType ?? NodeClientType.Unknown;

        public ConcurrentDictionary<AllocationContexts, int> AllocationSlots { get; init; }

        public Dictionary<AllocationContexts, int> AvailableAllocationSlots => AllocationSlots
            .Select(kv => (kv.Key, _allocationAllowances[kv.Key] - kv.Value))
            .ToDictionary();

        public AllocationContexts SleepingContexts { get; private set; }

        private ConcurrentDictionary<AllocationContexts, DateTime?> SleepingSince { get; } = new();

        public ISyncPeer SyncPeer { get; }

        public bool IsInitialized => SyncPeer.IsInitialized;

        public UInt256 TotalDifficulty => SyncPeer.TotalDifficulty;

        public long HeadNumber => SyncPeer.HeadNumber;

        public Hash256 HeadHash => SyncPeer.HeadHash;
        public bool HasAnyAllocation => AllocationSlots.Any(kv => kv.Value < _allocationAllowances[kv.Key]);

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool CanBeAllocated(AllocationContexts contexts)
        {
            return !IsAsleep(contexts) &&
                   !IsAllocationFull(contexts) &&
                   this.SupportsAllocation(contexts);
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool IsAsleep(AllocationContexts contexts)
        {
            return (contexts & SleepingContexts) != AllocationContexts.None;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool IsAllocationFull(AllocationContexts contexts)
        {
            return SeparateAllocationContexts(contexts).Any(aCtx => AllocationSlots[aCtx] <= 0);
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryAllocate(AllocationContexts contexts)
        {
            if (CanBeAllocated(contexts))
            {
                bool failed = false;
                AllocationContexts updatedCtx = AllocationContexts.None;

                foreach (AllocationContexts aCtx in SeparateAllocationContexts(contexts))
                {
                    int current = AllocationSlots[aCtx];
                    if (current <= 0 || !AllocationSlots.TryUpdate(aCtx, current - 1, current))
                    {
                        failed = true;
                        break;
                    }

                    updatedCtx |= aCtx;
                }

                if (failed)
                {
                    Free(updatedCtx);
                    return false;
                }

                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Free(AllocationContexts contexts)
        {
            foreach (AllocationContexts aCtx in SeparateAllocationContexts(contexts))
            {
                AllocationSlots[aCtx]++;
            }
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void PutToSleep(AllocationContexts contexts, DateTime dateTime)
        {
            SleepingContexts |= contexts;
            SleepingSince[contexts] = dateTime;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void TryToWakeUp(DateTime dateTime, TimeSpan wakeUpIfSleepsMoreThanThis)
        {
            foreach (KeyValuePair<AllocationContexts, DateTime?> keyValuePair in SleepingSince)
            {
                if (IsAsleep(keyValuePair.Key))
                {
                    if (dateTime - keyValuePair.Value >= wakeUpIfSleepsMoreThanThis)
                    {
                        WakeUp(keyValuePair.Key);
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        private void WakeUp(AllocationContexts allocationContexts)
        {
            SleepingContexts ^= allocationContexts;

            foreach (KeyValuePair<AllocationContexts, int> allocationIndex in AllocationIndexes)
            {
                if ((allocationContexts & allocationIndex.Key) == allocationIndex.Key)
                {
                    _weaknesses[allocationIndex.Value] = 0;
                }
            }

            SleepingSince.TryRemove(allocationContexts, out _);
        }

        // map from AllocationContexts single flag to index in array of _weaknesses
        private static readonly IDictionary<AllocationContexts, int> AllocationIndexes =
            FastEnum.GetValues<AllocationContexts>()
            .Where(c => c != AllocationContexts.All && c != AllocationContexts.None)
            .Select((a, i) => (a, i))
            .ToDictionary(v => v.a, v => v.i);

        private readonly int[] _weaknesses = new int[AllocationIndexes.Count];

        public const int SleepThreshold = 2;

        public AllocationContexts IncreaseWeakness(AllocationContexts allocationContexts)
        {
            AllocationContexts sleeps = AllocationContexts.None;

            foreach (KeyValuePair<AllocationContexts, int> allocationIndex in AllocationIndexes)
            {
                if ((allocationContexts & allocationIndex.Key) == allocationIndex.Key)
                {
                    ResolveWeaknessChecks(ref _weaknesses[allocationIndex.Value], allocationIndex.Key, ref sleeps);
                }
            }

            return sleeps;
        }

        private static void ResolveWeaknessChecks(ref int weakness, AllocationContexts singleContext, ref AllocationContexts sleeps)
        {
            int level = Interlocked.Increment(ref weakness);
            if (level >= SleepThreshold)
            {
                sleeps |= singleContext;
            }
        }

        private static string BuildContextString(AllocationContexts contexts)
        {
            return $"{((contexts & AllocationContexts.Headers) == AllocationContexts.Headers ? "H" : " ")}{((contexts & AllocationContexts.Bodies) == AllocationContexts.Bodies ? "B" : " ")}{((contexts & AllocationContexts.Receipts) == AllocationContexts.Receipts ? "R" : " ")}{((contexts & AllocationContexts.State) == AllocationContexts.State ? "N" : " ")}{((contexts & AllocationContexts.Snap) == AllocationContexts.Snap ? "S" : " ")}";
        }

        private string BuildSlotContextString()
        {
            StringBuilder ctxStringBuilder = new StringBuilder();

            (AllocationContexts, char)[] stringOrders =
            [
                (AllocationContexts.Headers, 'h'),
                (AllocationContexts.Bodies, 'b'),
                (AllocationContexts.Receipts, 'r'),
                (AllocationContexts.State, 'n'),
                (AllocationContexts.Snap, 's'),
            ];

            foreach ((AllocationContexts ctx, char chRep) in stringOrders)
            {
                int count = AllocationSlots[ctx];
                int allowances = _allocationAllowances[ctx];
                if (count == allowances)
                {
                    ctxStringBuilder.Append(' ');
                }
                else if (count == 0)
                {
                    ctxStringBuilder.Append(Char.ToUpper(chRep));
                }
                else if (allowances - count == 1)
                {
                    ctxStringBuilder.Append(chRep);
                }
                else
                {
                    ctxStringBuilder.Append(allowances - count);
                }
            }

            return ctxStringBuilder.ToString();
        }

        public override string ToString() => $"[{BuildSlotContextString()} ][{BuildContextString(SleepingContexts)} ]{SyncPeer}";

        private AllocationContexts[] SeparateAllocationContexts(AllocationContexts contexts)
        {
            if (SeparatedContextsCache.TryGetValue(contexts, out AllocationContexts[] cachedContext))
            {
                return cachedContext;
            }

            cachedContext = FastEnum.GetValues<AllocationContexts>()
                .Where(aCtx => IsOnlyOneContext(aCtx) && (contexts & aCtx) != 0)
                .ToArray();

            SeparatedContextsCache.TryAdd(contexts, cachedContext);
            return cachedContext;
        }

        private static ConcurrentDictionary<AllocationContexts, AllocationContexts[]> SeparatedContextsCache = new ConcurrentDictionary<AllocationContexts, AllocationContexts[]>();

        public static bool IsOnlyOneContext(AllocationContexts x)
        {
            return (x & (x - 1)) == 0;
        }
    }
}
