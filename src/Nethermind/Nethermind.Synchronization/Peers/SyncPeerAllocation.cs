// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Blockchain;
using Nethermind.Stats;
using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.Synchronization.Peers
{
    public class SyncPeerAllocation
    {
        public static SyncPeerAllocation FailedAllocation = new(NullStrategy.Instance, AllocationContexts.None);

        /// <summary>
        /// this should be used whenever we change IsAllocated property on PeerInfo-
        /// </summary>
        private static readonly object _allocationLock = new();

        private readonly IPeerAllocationStrategy _peerAllocationStrategy;
        public AllocationContexts Contexts { get; }

        [MemberNotNullWhen(true, nameof(HasPeer))]
        public PeerInfo? Current { get; private set; }

        public bool HasPeer => Current is not null;

        public SyncPeerAllocation(PeerInfo peerInfo, AllocationContexts contexts)
            : this(new StaticStrategy(peerInfo), contexts)
        {
        }

        public SyncPeerAllocation(IPeerAllocationStrategy peerAllocationStrategy, AllocationContexts contexts)
        {
            _peerAllocationStrategy = peerAllocationStrategy;
            Contexts = contexts;
        }

        public void AllocateBestPeer(
            IEnumerable<PeerInfo> peers,
            INodeStatsManager nodeStatsManager,
            IBlockTree blockTree)
        {
            PeerInfo? current = Current;
            PeerInfo? selected = _peerAllocationStrategy.Allocate(Current, peers, nodeStatsManager, blockTree);
            if (selected == current)
            {
                return;
            }

            AllocationChangeEventArgs? replacedArgs = null;
            lock (_allocationLock)
            {
                if (selected is not null && selected.TryAllocate(Contexts))
                {
                    Current = selected;
                    replacedArgs = new(current, selected);
                    current?.Free(Contexts);
                }
            }

            if (replacedArgs is not null)
            {
                Replaced?.Invoke(this, replacedArgs);
            }
        }

        public void Cancel()
        {
            PeerInfo? current = Current;
            if (current is null)
            {
                return;
            }

            lock (_allocationLock)
            {
                current.Free(Contexts);
                Current = null;
            }

            AllocationChangeEventArgs args = new(current, null);
            Cancelled?.Invoke(this, args);
        }

        public event EventHandler<AllocationChangeEventArgs>? Replaced;

        public event EventHandler<AllocationChangeEventArgs>? Cancelled;

        public override string ToString()
        {
            return $"[Allocation|{Current}]";
        }
    }
}
