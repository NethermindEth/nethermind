// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.TxPool;

public interface ITxPoolConfig : IConfig
{
    [ConfigItem(DefaultValue = "5", Description = "The average percentage of transaction hashes from persistent broadcast sent to a peer together with hashes of the last added transactions.")]
    int PeerNotificationThreshold { get; set; }

    [ConfigItem(DefaultValue = "2048", Description = "The max number of transactions held in the mempool (the more transactions in the mempool, the more memory used).")]
    int Size { get; set; }

    [ConfigItem(DefaultValue = "false", Description = "Whether to enable blob transactions.")]
    bool BlobSupportEnabled { get; set; }

    [ConfigItem(DefaultValue = "false", Description = "Whether to store blob transactions in the database.")]
    bool PersistentBlobStorageEnabled { get; set; }

    [ConfigItem(DefaultValue = "16384", Description = "The max number of full blob transactions stored in the database (increasing the number of transactions in the blob pool also results in higher memory usage). The default value uses max 13GB for 6 blobs where one blob is 2GB (16386 * 128KB).")]
    int PersistentBlobStorageSize { get; set; }

    [ConfigItem(DefaultValue = "256", Description = "The max number of full blob transactions cached in memory. The default value uses max 200MB for 6 blobs where one blob is 33MB (256 * 128KB)")]
    int BlobCacheSize { get; set; }

    [ConfigItem(DefaultValue = "512", Description = "The max number of full blob transactions stored in memory. Used only if persistent storage is disabled.")]
    int InMemoryBlobPoolSize { get; set; }

    [ConfigItem(DefaultValue = "0", Description = "The max number of pending transactions per single sender. `0` to lift the limit.")]
    int MaxPendingTxsPerSender { get; set; }

    [ConfigItem(DefaultValue = "16", Description = "The max number of pending blob transactions per single sender. `0` to lift the limit.")]
    int MaxPendingBlobTxsPerSender { get; set; }

    [ConfigItem(DefaultValue = "524288",
        Description = "The max number of cached hashes of already known transactions. Set automatically by the memory hint.")]
    int HashCacheSize { get; set; }

    [ConfigItem(DefaultValue = "null",
        Description = "The max transaction gas allowed.")]

    long? GasLimit { get; set; }

    [ConfigItem(DefaultValue = "null",
        Description = "The current transaction pool state reporting interval, in minutes.")]
    int? ReportMinutes { get; set; }
}
