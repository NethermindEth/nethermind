// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.StateComposition;

[ConfigCategory(Description = "State composition metrics for bloatnet benchmarking")]
public interface IStateCompositionConfig : IConfig
{
    [ConfigItem(Description = "Enable state composition plugin", DefaultValue = "true")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "Timeout in seconds for queued scan requests", DefaultValue = "5")]
    int ScanQueueTimeoutSeconds { get; set; }

    [ConfigItem(Description = "Max parallel threads for baseline trie scan", DefaultValue = "4")]
    int ScanParallelism { get; set; }

    [ConfigItem(Description = "Memory budget for baseline scan in bytes (1GB default)",
        DefaultValue = "1000000000")]
    long ScanMemoryBudget { get; set; }
}
