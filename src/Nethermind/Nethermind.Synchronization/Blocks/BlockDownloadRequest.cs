// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

        public int? NumberOfLatestBlocksToBeIgnored { get; }
        public DownloaderOptions Options { get; }

        public override string ToString()
        {
            return $"Blocks Request: {Options}";
        }
    }
}
