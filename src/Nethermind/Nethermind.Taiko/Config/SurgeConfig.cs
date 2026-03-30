// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Taiko.Config;

public class SurgeConfig : ISurgeConfig
{
    public string? L1EthApiEndpoint { get; set; }
    public ulong BlocksPerBatch { get; set; } = 1800;
    public ulong TargetBlobCount { get; set; } = 3;
    public ulong L2BlockGasTarget { get; set; } = 40_000;
    public ulong FixedProposalGas { get; set; } = 75_000;
    public ulong FixedProposalGasWithFullInboxBuffer { get; set; } = 50_000;
    public ulong FixedProvingGas { get; set; } = 30_000;
    public int FeeHistoryBlockCount { get; set; } = 200;
    public int L2GasUsageWindowSize { get; set; } = 20;
    public ulong EstimatedOffchainProvingCost { get; set; } = 1_833_333_333_333_333;
    public string? TaikoInboxAddress { get; set; }
    public int AverageGasUsagePercentage { get; set; } = 80;
    public int SharingPercentage { get; set; } = 75;
    public int BoostBaseFeePercentage { get; set; } = 5;
    public int GasPriceRefreshTimeoutSeconds { get; set; } = 12;
    public int MaxGasLimitRatio { get; set; } = 0;
    public bool TdxEnabled { get; set; } = false;
}
