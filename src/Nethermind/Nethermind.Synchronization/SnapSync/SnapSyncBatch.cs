// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.State.Snap;

namespace Nethermind.Synchronization.SnapSync
{
    public class SnapSyncBatch : IDisposable
    {
        public AccountRange? AccountRangeRequest { get; set; }
        public AccountsAndProofs? AccountRangeResponse { get; set; }

        public StorageRange? StorageRangeRequest { get; set; }
        public SlotsAndProofs? StorageRangeResponse { get; set; }

        public IOwnedReadOnlyList<ValueHash256>? CodesRequest { get; set; }
        public IOwnedReadOnlyList<byte[]>? CodesResponse { get; set; }

        public AccountsToRefreshRequest? AccountsToRefreshRequest { get; set; }
        public IOwnedReadOnlyList<byte[]>? AccountsToRefreshResponse { get; set; }

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
