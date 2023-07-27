// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.TxPool
{
    public interface ITxPoolConfig : IConfig
    {
        [ConfigItem(DefaultValue = "5", Description = "Defines average percent of tx hashes from persistent broadcast send to peer together with hashes of last added txs.")]
        int PeerNotificationThreshold { get; set; }

        [ConfigItem(DefaultValue = "2048", Description = "Max number of transactions held in mempool (more transactions in mempool mean more memory used")]
        int Size { get; set; }

        [ConfigItem(DefaultValue = "false", Description = "If true, all blob transactions would be stored in persistent db")]
        bool PersistentBlobStorageEnabled { get; set; }

        [ConfigItem(DefaultValue = "131072", Description = "Max number of full blob transactions stored in db (but more transactions in blob pool mean more memory use too")]
        int PersistentBlobStorageSize { get; set; }

        [ConfigItem(DefaultValue = "256", Description = "Max number of full blob transactions stored in memory as a cache for persistent storage")]
        int BlobCacheSize { get; set; }

        [ConfigItem(DefaultValue = "512", Description = "Max number of full blob transactions stored in memory. Used only if persistent storage is disabled")]
        int InMemoryBlobPoolSize { get; set; }

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
