// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Taiko.Config;

public interface ISurgeConfig : IConfig
{
    [ConfigItem(Description = "The URL of the L1 execution node JSON-RPC API.", DefaultValue = "null")]
    string? L1EthApiEndpoint { get; set; }

    [ConfigItem(Description = "L2 gas per L2 batch for gas price calculation.", DefaultValue = "1000000")]
    ulong L2GasPerL2Batch { get; set; }

    [ConfigItem(Description = "Proving cost per L2 batch in wei.", DefaultValue = "800000000000000")]
    ulong ProvingCostPerL2Batch { get; set; }

    [ConfigItem(Description = "L1 gas needed for posting a batch as a blob.", DefaultValue = "180000")]
    ulong BatchPostingGasWithoutCallData { get; set; }

    [ConfigItem(Description = "L1 gas needed for posting a batch as calldata.", DefaultValue = "260000")]
    ulong BatchPostingGasWithCallData { get; set; }

    [ConfigItem(Description = "L1 gas needed to post and verify proof.", DefaultValue = "750000")]
    ulong ProofPostingGas { get; set; }

    [ConfigItem(Description = "Number of blocks to consider for computing the L1 average base fee.", DefaultValue = "200")]
    int FeeHistoryBlockCount { get; set; }

    [ConfigItem(Description = "Number of recent L2 batches to consider for computing the moving average of gas usage.", DefaultValue = "20")]
    int L2GasUsageWindowSize { get; set; }

    [ConfigItem(Description = "The address of the TaikoInbox contract.", DefaultValue = "null")]
    string? TaikoInboxAddress { get; set; }

    [ConfigItem(Description = "Percentage of the base fee that is shared with the L2 batch submitter.", DefaultValue = "75")]
    int SharingPercentage { get; set; }

    [ConfigItem(Description = "Percentage of the base fee that is used for boosting.", DefaultValue = "5")]
    int BoostBaseFeePercentage { get; set; }

    [ConfigItem(Description = "Maximum time in seconds to use cached gas price estimates before forcing a refresh.", DefaultValue = "12")]
    int GasPriceRefreshTimeoutSeconds { get; set; }
}
