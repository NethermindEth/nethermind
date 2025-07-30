// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Config;

public interface IHistoryConfig : IConfig
{
    bool Enabled { get; }

    // For EIP-4444 set default to 82125
    [ConfigItem(
        Description = "The number of epochs to retain historical blocks and receipts. Set to null for unlimited retention. For mainnet this must be at least 82125.",
        DefaultValue = "null")]
    long? RetentionEpochs { get; set; }

    [ConfigItem(
        Description = "Whether to drop pre-merge blocks and receipts.",
        DefaultValue = "false")]
    bool DropPreMerge { get; set; }

    [ConfigItem(
        Description = "Run history pruner every x times.",
        DefaultValue = "1")]
    int RunEvery { get; set; }
}
