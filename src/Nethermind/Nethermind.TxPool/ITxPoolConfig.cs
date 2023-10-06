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
