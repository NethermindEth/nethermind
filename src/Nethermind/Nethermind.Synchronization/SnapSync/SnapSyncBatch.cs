// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.State.Snap;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Synchronization.SnapSync
{
    public class SnapSyncBatch : IDisposable, IDispatchTerminal
    {
        internal SnapRecoveryCoordinator.Lease? RecoveryLease { get; set; }
        internal SnapRecoveryCoordinator.Cohort? RecoveryCohort { get; set; }
        internal SnapProvider.StorageEvidence? DispatchEvidence { get; set; }
        internal bool LocalDeferred { get; set; }
        internal SnapRecoveryCoordinator? RecoveryContext { get; set; }
        internal ISnapProvider? ProviderContext { get; set; }
        internal bool DispatchOwned { get; private set; }
        private Action? _terminal;

        void IDispatchTerminal.PrepareDispatch(Synchronization.Peers.ISyncPeerPool peerPool)
        {
            DispatchOwned = true;
            if (RecoveryContext is not null) RecoveryContext.PeerPool = peerPool;
        }

        internal void SetTerminal(Action terminal) => _terminal = terminal;

        void IDispatchTerminal.CompleteDispatch()
        {
            try { Interlocked.Exchange(ref _terminal, null)?.Invoke(); }
            finally
            {
                RecoveryLease?.Complete();
                RecoveryCohort?.Complete();
                RecoveryLease = null;
                RecoveryCohort = null;
                DispatchEvidence = null;
                RecoveryContext = null;
                ProviderContext = null;
            }
        }

        internal void Handled() => Interlocked.Exchange(ref _terminal, null);
        internal long ResponseMilliseconds { get; set; }

        public AccountRange? AccountRangeRequest { get; set; }
        public AccountsAndProofs? AccountRangeResponse { get; set; }

        public StorageRange? StorageRangeRequest { get; set; }
        public SlotsAndProofs? StorageRangeResponse { get; set; }

        public IOwnedReadOnlyList<ValueHash256>? CodesRequest { get; set; }
        public IByteArrayList? CodesResponse { get; set; }

        public AccountsToRefreshRequest? AccountsToRefreshRequest { get; set; }
        public AccountsAndProofs? AccountsToRefreshResponse { get; set; }

        public override string ToString()
        {
            if (AccountRangeRequest is not null)
            {
                return AccountRangeRequest!.ToString();
            }
            else if (StorageRangeRequest is not null)
            {
                return StorageRangeRequest!.ToString();
            }
            else if (CodesRequest is not null)
            {
                return $"CodesRequest: ({CodesRequest.Count})";
            }
            else if (AccountsToRefreshRequest is not null)
            {
                return AccountsToRefreshRequest!.ToString();
            }
            else
            {
                return "Empty snap sync batch";
            }
        }

        public void Dispose()
        {
            AccountRangeResponse?.Dispose();
            StorageRangeResponse?.Dispose();
            CodesResponse?.Dispose();
            AccountsToRefreshResponse?.Dispose();
            CodesRequest?.Dispose();
            StorageRangeRequest?.Dispose();
            AccountsToRefreshRequest?.Dispose();
        }
    }
}
