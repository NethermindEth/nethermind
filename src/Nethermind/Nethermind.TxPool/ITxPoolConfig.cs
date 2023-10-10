// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.TxPool
{
    public interface ITxPoolConfig : IConfig
    {
        [ConfigItem(DefaultValue = "5", Description = "Defines average percent of tx hashes from persistent broadcast send to peer together with hashes of last added txs. Set this value to 100 if you want to broadcast all transactions.")]
        int PeerNotificationThreshold { get; set; }

        [ConfigItem(DefaultValue = "2048", Description = "Max number of transactions held in mempool (more transactions in mempool mean more memory used")]
        int Size { get; set; }

        [ConfigItem(DefaultValue = "false", Description = "If true, blob transactions support will be enabled")]
        bool BlobSupportEnabled { get; set; }

        [ConfigItem(DefaultValue = "false", Description = "If true, all blob transactions would be stored in persistent db")]
        bool PersistentBlobStorageEnabled { get; set; }

        [ConfigItem(DefaultValue = "16384", Description = "Max number of full blob transactions stored in the database (increasing the number of transactions in the blob pool also results in higher memory usage). Default value use max 13GB (16386*128KB*6blobs), for 1-blob txs it's 2GB (16386*128KB)")]
        int PersistentBlobStorageSize { get; set; }

        [ConfigItem(DefaultValue = "256", Description = "Max number of full blob transactions stored in memory as a cache for persistent storage. Default value use max 200MB (256*128KB*6blobs), for 1-blob txs it's 33MB (256*128KB)")]
        int BlobCacheSize { get; set; }

        [ConfigItem(DefaultValue = "512", Description = "Max number of full blob transactions stored in memory. Used only if persistent storage is disabled")]
        int InMemoryBlobPoolSize { get; set; }

        [ConfigItem(DefaultValue = "0", Description = "Max number of pending transactions per single sender. Set it to 0 to disable the limit.")]
        int MaxPendingTxsPerSender { get; set; }

        [ConfigItem(DefaultValue = "16", Description = "Max number of pending blob transactions per single sender. Set it to 0 to disable the limit.")]
        int MaxPendingBlobTxsPerSender { get; set; }

        [ConfigItem(DefaultValue = "524288",
            Description = "Max number of cached hashes of already known transactions." +
                          "It is set automatically by the memory hint.")]
        int HashCacheSize { get; set; }

        [ConfigItem(DefaultValue = "null",
            Description = "Max transaction gas allowed.")]

        long? GasLimit { get; set; }

        [ConfigItem(DefaultValue = "null",
            Description = "Minutes between reporting on current state of tx pool.")]
        int? ReportMinutes { get; set; }
    }
}
