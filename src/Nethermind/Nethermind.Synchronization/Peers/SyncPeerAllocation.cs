// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Nethermind.Synchronization.Peers
{
    public class SyncPeerAllocation(AllocationContexts contexts, Lock? allocationLock = null) : IDisposable
    {
        /// <summary>
        /// this should be used whenever we change IsAllocated property on PeerInfo-
        /// </summary>
        private readonly Lock? _allocationLock = allocationLock ?? new Lock();
        private readonly Action? _onDisposed;
        private bool _disposed;

        internal SyncPeerAllocation(AllocationContexts contexts, Lock? allocationLock, Action onDisposed)
            : this(contexts, allocationLock) => _onDisposed = onDisposed;

        private AllocationContexts Contexts { get; } = contexts;

        [MemberNotNullWhen(true, nameof(HasPeer))]
        public PeerInfo? Current { get; private set; }

        public bool HasPeer => Current is not null;

        public void AllocatePeer(PeerInfo? selected)
        {
            lock (_allocationLock)
            {
                if (_disposed || selected == Current)
                {
                    return;
                }

                if (selected is not null && selected.TryAllocate(Contexts))
                {
                    PeerInfo? current = Current;
                    Current = selected;
                    current?.Free(Contexts);
                }
            }
        }

        /// <summary>
        /// Returns the allocated peer slot. Repeated calls have no effect.
        /// </summary>
        public void Dispose()
        {
            bool released = false;
            lock (_allocationLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                PeerInfo? current = Current;
                if (current is not null)
                {
                    current.Free(Contexts);
                    Current = null;
                    released = true;
                }
            }

            // A peer-less disposal must not wake every allocator on zero-timeout polling paths.
            if (released)
            {
                _onDisposed?.Invoke();
            }
        }

        public override string ToString() => $"[Allocation|{Current}]";
    }
}
