// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Nethermind.Synchronization.Peers
{
    public class SyncPeerAllocation(AllocationContexts contexts, Lock? allocationLock = null)
    {
        public static SyncPeerAllocation FailedAllocation = new(AllocationContexts.None, null);

        /// <summary>
        /// this should be used whenever we change IsAllocated property on PeerInfo-
        /// </summary>
        private readonly Lock? _allocationLock = allocationLock ?? new Lock();

        private AllocationContexts Contexts { get; } = contexts;

        [MemberNotNullWhen(true, nameof(HasPeer))]
        public PeerInfo? Current { get; private set; }

        public bool HasPeer => Current is not null;

        public SyncPeerAllocation(PeerInfo peerInfo, AllocationContexts contexts, Lock? allocationLock = null)

            : this(contexts, allocationLock) => Current = peerInfo;

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

        public override string ToString() => $"[Allocation|{Current}]";
    }
}
