// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Stats;
using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.Synchronization.Peers
{
    public class SyncPeerAllocation
    {
        public static SyncPeerAllocation FailedAllocation = new(AllocationContexts.None, null);

        /// <summary>
        /// this should be used whenever we change IsAllocated property on PeerInfo-
        /// </summary>
        private readonly Lock? _allocationLock;

        private AllocationContexts Contexts { get; }

        [MemberNotNullWhen(true, nameof(HasPeer))]
        public PeerInfo? Current { get; private set; }

        public bool HasPeer => Current is not null;

        public SyncPeerAllocation(PeerInfo peerInfo, AllocationContexts contexts, Lock? allocationLock = null)

            : this(contexts, allocationLock)
        {
            Current = peerInfo;
        }

        public SyncPeerAllocation(AllocationContexts contexts, Lock? allocationLock = null)
        {
            Contexts = contexts;
            _allocationLock = allocationLock ?? new Lock();
        }

        public void AllocatePeer(PeerInfo? selected)
        {
            PeerInfo? current = Current;
            if (selected == current)
            {
                return;
            }

            lock (_allocationLock)
            {
                if (selected is not null && selected.TryAllocate(Contexts))
                {
                    Current = selected;
                    current?.Free(Contexts);
                }
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
        }

        public override string ToString()
        {
            return $"[Allocation|{Current}]";
        }
    }
}
