// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Taiko.Config;

public class SurgeConfig : ISurgeConfig
{
    public string? L1EthApiEndpoint { get; set; }
    public ulong L2GasPerL2Batch { get; set; } = 1_000_000;
    public ulong ProvingCostPerL2Batch { get; set; } = 800_000_000_000_000;
    public ulong BatchPostingGasWithoutCallData { get; set; } = 180_000;
    public ulong BatchPostingGasWithCallData { get; set; } = 260_000;
    public ulong ProofPostingGas { get; set; } = 750_000;
    public int FeeHistoryBlockCount { get; set; } = 200;
    public int L2GasUsageWindowSize { get; set; } = 20;
    public string? TaikoInboxAddress { get; set; }
    public int AverageGasUsagePercentage { get; set; } = 80;
    public int SharingPercentage { get; set; } = 75;
    public int BoostBaseFeePercentage { get; set; } = 5;
    public int GasPriceRefreshTimeoutSeconds { get; set; } = 12;
}
