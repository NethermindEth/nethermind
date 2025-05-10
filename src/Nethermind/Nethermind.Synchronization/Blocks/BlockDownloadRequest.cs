// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Synchronization.Blocks
{
    public class BlocksRequest : IDisposable
    {
        public IOwnedReadOnlyList<BlockHeader> BodiesRequests { get; set; } = new ArrayPoolList<BlockHeader>(0);
        public OwnedBlockBodies? OwnedBodies { get; set; }
        public IOwnedReadOnlyList<BlockHeader> ReceiptsRequests { get; set; } = new ArrayPoolList<BlockHeader>(0);
        public IOwnedReadOnlyList<TxReceipt[]?>? Receipts { get; set; }

        public int? NumberOfLatestBlocksToBeIgnored { get; }
        public Task DownloadTask { get; set; }
        bool _disposed;

        public override string ToString()
        {
            return $"Blocks Request: {BodiesRequests.Count} Bodies, {ReceiptsRequests.Count} Receipts";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            BodiesRequests?.Dispose();
            ReceiptsRequests?.Dispose();
            OwnedBodies?.Dispose();
            Receipts?.Dispose();
            DownloadTask?.Dispose();
        }
    }
}
