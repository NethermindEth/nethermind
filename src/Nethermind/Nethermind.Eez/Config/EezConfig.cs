// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Posting;
using Nethermind.Eez.Sequencer;

namespace Nethermind.Eez.Config;

public class EezConfig : IEezConfig
{
    public bool Enabled { get; set; }
    public bool FollowerEnabled { get; set; }
    public string? SequencerRpcUrl { get; set; }
    public string? L1RpcUrl { get; set; }
    public ulong L1ChainId { get; set; }
    public string? RegistryAddress { get; set; }
    public ulong RegistryDeployBlock { get; set; }
    public ulong RollupId { get; set; }
    public ulong L2BlockTimeSeconds { get; set; } = 2;
    public ulong L2GasLimit { get; set; } = EezSettlementContext.DefaultGasLimit;
    public int L1PollingIntervalMs { get; set; } = 2000;
    public ulong L1LogScanBlocks { get; set; } = 100_000;
    public bool SequencerEnabled { get; set; }
    public uint L1BlockTimeMs { get; set; } = 12_000;
    public uint ProofTimeMs { get; set; } = 2_000;
    public uint SubmissionSlackMs { get; set; } = 100;
    public string[] Provers { get; set; } = [];
    public int AttestationGraceMs { get; set; } = 250;
    public string? PosterAddress { get; set; }
    public string? L1BuilderRpcUrl { get; set; }
    public ulong PostBatchPriorityFee { get; set; } = 10_000_000_000;
    public ulong MaxPostBatchGas { get; set; } = PostBatchGas.DefaultLimit;
    public ulong MaxBlocksPerBatch { get; set; } = RollupTiming.MaxBlocksPerCatchup;
    public ulong MaxSpeculativeDepth { get; set; } = 64;
    public string? SequencerFeeRecipient { get; set; }
}
