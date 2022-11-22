// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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

        public Keccak[]? CodesRequest { get; set; }
        public byte[][]? CodesResponse { get; set; }

        public AccountsToRefreshRequest? AccountsToRefreshRequest { get; set; }
        public byte[][]? AccountsToRefreshResponse { get; set; }
    }
}
