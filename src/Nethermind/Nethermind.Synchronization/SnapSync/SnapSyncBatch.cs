// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.State.Snap;

namespace Nethermind.Synchronization.SnapSync
{
    public class SnapSyncBatch
    {
        public AccountRange? AccountRangeRequest { get; set; }
        public AccountsAndProofs? AccountRangeResponse { get; set; }

        public StorageRange? StorageRangeRequest { get; set; }
        public SlotsAndProofs? StorageRangeResponse { get; set; }

        public ValueKeccak[]? CodesRequest { get; set; }
        public byte[][]? CodesResponse { get; set; }

        public AccountsToRefreshRequest? AccountsToRefreshRequest { get; set; }
        public byte[][]? AccountsToRefreshResponse { get; set; }

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
                return $"CodesRequest: ({CodesRequest.Length})";
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
    }
}
