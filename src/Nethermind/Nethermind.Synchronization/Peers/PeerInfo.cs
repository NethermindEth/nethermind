//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Dirichlet.Numerics;

[assembly: InternalsVisibleTo("Nethermind.Synchronization.Test")]

namespace Nethermind.Synchronization.Peers
{
    public class PeerInfo
    {
        public PeerInfo(ISyncPeer syncPeer)
        {
            SyncPeer = syncPeer;
            RecognizeClientType(syncPeer);
        }

        public PeerClientType PeerClientType { get; private set; }

        public AllocationContexts AllocatedContexts { get; private set; }

        public AllocationContexts SleepingContexts { get; private set; }

        private ConcurrentDictionary<AllocationContexts, DateTime?> SleepingSince { get; } = new ConcurrentDictionary<AllocationContexts, DateTime?>();

        public ISyncPeer SyncPeer { get; }

        public bool IsInitialized => SyncPeer.IsInitialized;

        public UInt256 TotalDifficulty => SyncPeer.TotalDifficulty;

        public long HeadNumber => SyncPeer.HeadNumber;

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool CanBeAllocated(AllocationContexts contexts)
        {
            return !IsAsleep(contexts) &&
                   !IsAllocated(contexts);
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

            if ((allocationContexts & AllocationContexts.Headers) == AllocationContexts.Headers)
            {
                _headersWeakness = 0;
            }

            if ((allocationContexts & AllocationContexts.Bodies) == AllocationContexts.Bodies)
            {
                _bodiesWeakness = 0;
            }

            if ((allocationContexts & AllocationContexts.Receipts) == AllocationContexts.Receipts)
            {
                _receiptsWeakness = 0;
            }

            if ((allocationContexts & AllocationContexts.State) == AllocationContexts.State)
            {
                _stateWeakness = 0;
            }

            SleepingSince.TryRemove(allocationContexts, out _);
        }

        private int _receiptsWeakness;
        private int _bodiesWeakness;
        private int _headersWeakness;
        private int _stateWeakness;

        public const int SleepThreshold = 2;

        public AllocationContexts IncreaseWeakness(AllocationContexts allocationContexts)
        {
            AllocationContexts sleeps = AllocationContexts.None;
            if ((allocationContexts & AllocationContexts.Headers) == AllocationContexts.Headers)
            {
                ResolveWeaknessChecks(ref _headersWeakness, AllocationContexts.Headers, ref sleeps);
            }

            if ((allocationContexts & AllocationContexts.Bodies) == AllocationContexts.Bodies)
            {
                ResolveWeaknessChecks(ref _bodiesWeakness, AllocationContexts.Bodies, ref sleeps);
            }

            if ((allocationContexts & AllocationContexts.Receipts) == AllocationContexts.Receipts)
            {
                ResolveWeaknessChecks(ref _receiptsWeakness, AllocationContexts.Receipts, ref sleeps);
            }

            if ((allocationContexts & AllocationContexts.State) == AllocationContexts.State)
            {
                ResolveWeaknessChecks(ref _stateWeakness, AllocationContexts.State, ref sleeps);
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
            return $"{((contexts & AllocationContexts.Headers) == AllocationContexts.Headers ? "H" : " ")}{((contexts & AllocationContexts.Bodies) == AllocationContexts.Bodies ? "B" : " ")}{((contexts & AllocationContexts.Receipts) == AllocationContexts.Receipts ? "R" : " ")}{((contexts & AllocationContexts.State) == AllocationContexts.State ? "S" : " ")}";
        }
        
        public override string ToString() => $"[{BuildContextString(AllocatedContexts)}][{BuildContextString(SleepingContexts)}]{SyncPeer}";

        private void RecognizeClientType(ISyncPeer syncPeer)
        {
            if (syncPeer == null)
            {
                PeerClientType = PeerClientType.Unknown;
            }
            else if (syncPeer.ClientId?.Contains("BeSu", StringComparison.InvariantCultureIgnoreCase) ?? false)
            {
                PeerClientType = PeerClientType.BeSu;
            }
            else if (syncPeer.ClientId?.Contains("Geth", StringComparison.InvariantCultureIgnoreCase) ?? false)
            {
                PeerClientType = PeerClientType.Geth;
            }
            else if (syncPeer.ClientId?.Contains("Nethermind", StringComparison.InvariantCultureIgnoreCase) ?? false)
            {
                PeerClientType = PeerClientType.Nethermind;
            }
            else if (syncPeer.ClientId?.Contains("Parity", StringComparison.InvariantCultureIgnoreCase) ?? false)
            {
                PeerClientType = PeerClientType.Parity;
            }
            else
            {
                PeerClientType = PeerClientType.Unknown;
            }
        }
    }
}