// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.TxPool
{
    public class TxPoolConfig : ITxPoolConfig
    {
        public int PeerNotificationThreshold { get; set; } = 5;
        public int Size { get; set; } = 2048;
        public bool BlobSupportEnabled { get; set; } = true;
        public bool PersistentBlobStorageEnabled { get; set; } = false;
        public int PersistentBlobStorageSize { get; set; } = 16 * 1024; // theoretical max - 12,5GB (128KB * 6 * 16384); for one-blob txs - 2GB (128KB * 1 * 16384);
                                                                        // practical max - something between, but closer to 2GB than 12GB. Geth is limiting it to 10GB.
                                                                        // every day about 21600 blobs will be included (7200 blocks per day * 3 blob target)
        public int BlobCacheSize { get; set; } = 256;
        public int InMemoryBlobPoolSize { get; set; } = 512; // it is used when persistent pool is disabled
        public bool BlobReorgsSupportEnabled { get; set; } = true;
        public int MaxPendingTxsPerSender { get; set; } = 0;
        public int MaxPendingBlobTxsPerSender { get; set; } = 16;
        public int HashCacheSize { get; set; } = 512 * 1024;
        public long? GasLimit { get; set; } = null;
        public int? ReportMinutes { get; set; } = null;
    }
}
