// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
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
        public PeerInfo(ISyncPeer syncPeer)
        {
            SyncPeer = syncPeer;
        }

        public NodeClientType PeerClientType => SyncPeer?.ClientType ?? NodeClientType.Unknown;

        public AllocationContexts AllocatedContexts { get; private set; }

        public AllocationContexts SleepingContexts { get; private set; }

        private ConcurrentDictionary<AllocationContexts, DateTime?> SleepingSince { get; } = new();

        public ISyncPeer SyncPeer { get; }

        public bool IsInitialized => SyncPeer.IsInitialized;

        public UInt256 TotalDifficulty => SyncPeer.TotalDifficulty;

        public long HeadNumber => SyncPeer.HeadNumber;

        public Keccak HeadHash => SyncPeer.HeadHash;

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool CanBeAllocated(AllocationContexts contexts)
        {
            return !IsAsleep(contexts) &&
                   !IsAllocated(contexts) &&
                   this.SupportsAllocation(contexts);
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool IsAsleep(AllocationContexts contexts)
        {
            return (contexts & SleepingContexts) != AllocationContexts.None;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool IsAllocated(AllocationContexts contexts)
        {
            return (contexts & AllocatedContexts) != AllocationContexts.None;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryAllocate(AllocationContexts contexts)
        {
            if (CanBeAllocated(contexts))
            {
                AllocatedContexts |= contexts;
                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Free(AllocationContexts contexts)
        {
            AllocatedContexts ^= contexts;
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

        private void ResolveWeaknessChecks(ref int weakness, AllocationContexts singleContext, ref AllocationContexts sleeps)
        {
            int level = Interlocked.Increment(ref weakness);
            if (level >= SleepThreshold)
            {
                sleeps |= singleContext;
            }
        }

        private static string BuildContextString(AllocationContexts contexts)
        {
            return $"{((contexts & AllocationContexts.Headers) == AllocationContexts.Headers ? "H" : " ")}{((contexts & AllocationContexts.Bodies) == AllocationContexts.Bodies ? "B" : " ")}{((contexts & AllocationContexts.Receipts) == AllocationContexts.Receipts ? "R" : " ")}{((contexts & AllocationContexts.State) == AllocationContexts.State ? "N" : " ")}{((contexts & AllocationContexts.Snap) == AllocationContexts.Snap ? "S" : " ")}{((contexts & AllocationContexts.Witness) == AllocationContexts.Witness ? "W" : " ")}";
        }

        public override string ToString() => $"[{BuildContextString(AllocatedContexts)}][{BuildContextString(SleepingContexts)}]{SyncPeer}";
    }
}
