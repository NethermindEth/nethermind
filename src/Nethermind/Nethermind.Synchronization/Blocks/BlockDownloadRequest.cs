// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Synchronization.Blocks
{
    public class BlocksRequest
    {
        public BlocksRequest(DownloaderOptions options, int? numberOfLatestBlocksToBeIgnored)
        {
            Options = options;
            NumberOfLatestBlocksToBeIgnored = numberOfLatestBlocksToBeIgnored;
        }

        public BlocksRequest(DownloaderOptions options)
        {
            Options = options;
        }

        public BlocksRequest()
        {
        }

        public IList<BlockHeader> BodiesRequests { get; set; } = new List<BlockHeader>();
        public OwnedBlockBodies? OwnedBodies { get; set; }
        public BlockBody?[]? Bodies => OwnedBodies?.Bodies;
        public IList<BlockHeader> ReceiptsRequests { get; set; } = new List<BlockHeader>();
        public IOwnedReadOnlyList<TxReceipt[]?>? Receipts { get; set; }

        public int? NumberOfLatestBlocksToBeIgnored { get; }
        public DownloaderOptions Options { get; }
        public Task DownloadTask { get; set; }

        public override string ToString()
        {
            return $"Blocks Request: {Options}";
        }
    }
}
