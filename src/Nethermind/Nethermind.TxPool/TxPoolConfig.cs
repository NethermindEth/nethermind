// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.TxPool
{
    public class TxPoolConfig : ITxPoolConfig
    {
        public int PeerNotificationThreshold { get; set; } = 5;
        public int Size { get; set; } = 2048;
        public bool PersistentBlobStorageEnabled { get; set; } = false;
        public int PersistentBlobStorageSize { get; set; } = 128 * 1024; // we need some limit of blob txs, but extremely high to be limitless in practice
        public int BlobCacheSize { get; set; } = 256;
        public int InMemoryBlobPoolSize { get; set; } = 512; // it is used when persistent pool is disabled
        public int HashCacheSize { get; set; } = 512 * 1024;
        public long? GasLimit { get; set; } = null;
        public int? ReportMinutes { get; set; } = null;
    }
}
