// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db
{
    public static class DbNames
    {
        public const string Storage = "storage";
        public const string State = "state";
        public const string Code = "code";
        public const string Blocks = "blocks";
        public const string Headers = "headers";
        public const string BlockNumbers = "blockNumbers";
        public const string BlockAccessLists = "blockAccessLists";
        public const string Receipts = "receipts";
        public const string BlockInfos = "blockInfos";
        public const string BadBlocks = "badBlocks";
        public const string Bloom = "bloom";
        public const string Metadata = "metadata";
        public const string BlobTransactions = "blobTransactions";
        public const string DiscoveryNodes = "discoveryNodes";
        public const string DiscoveryV5Nodes = "discoveryV5Nodes";
        public const string PeersDb = "peers";
    }
}
