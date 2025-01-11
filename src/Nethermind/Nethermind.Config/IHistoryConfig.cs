// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Config;

public interface IHistoryConfig : IConfig
{
    // For EIP-4444 set default to 82125
    [ConfigItem(
        Description = "The number of epochs to retain historical blocks and receipts. Set to null for unlimited retention.",
        DefaultValue = "null")]
    ulong? HistoryPruneEpochs { get; set; }

    [ConfigItem(
        Description = "Whether to drop pre-merge blocks and receipts.",
        DefaultValue = "false")]
    bool DropPreMerge { get; set; }
}
