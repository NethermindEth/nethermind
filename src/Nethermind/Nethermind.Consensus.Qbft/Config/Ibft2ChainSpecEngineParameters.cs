// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.Qbft.Config;

/// <summary>
/// Besu's <c>ibft2</c> configuration section, the IBFT 2.0 engine.
/// </summary>
/// <remarks>
/// The header rules, hashing, validator voting and proposer rotation are the same as QBFT's, so this
/// engine reuses the shared <c>Bft*</c> components with the IBFT 2.0 extra-data codec. What differs is
/// the consensus wire protocol (<c>IBF/1</c> against QBFT's <c>istanbul/100</c>), which is only needed
/// to take part in consensus: a node following an IBFT 2.0 chain validates and executes its blocks
/// without it. Block production is therefore not offered on this engine.
/// <para>
/// A chain migrated from IBFT 2.0 to QBFT keeps both sections in its genesis and is a QBFT chain; see
/// <see cref="QbftChainSpecEngineParameters.StartBlock"/> and <see cref="Ibft2Parameters"/>.
/// </para>
/// </remarks>
public class Ibft2ChainSpecEngineParameters : IChainSpecEngineParameters
{
    public const string Engine = "ibft2";

    public string EngineName => Engine;
    public string SealEngineType => Core.SealEngineType.Ibft2;

    /// <summary>Blocks between epoch blocks, which reset outstanding validator votes. Besu default 30000.</summary>
    public long EpochLength { get; set; } = QbftChainSpecEngineParameters.DefaultEpochLength;

    /// <summary>Minimum seconds between blocks. Besu default 1.</summary>
    public int BlockPeriodSeconds { get; set; } = QbftChainSpecEngineParameters.DefaultBlockPeriodSeconds;

    /// <summary>Seconds a proposer waits before proposing a block with no transactions; 0 proposes immediately.</summary>
    public int EmptyBlockPeriodSeconds { get; set; }

    /// <summary>Test-only sub-second block period in milliseconds; 0 uses <see cref="BlockPeriodSeconds"/>.</summary>
    public long XBlockPeriodMilliseconds { get; set; }

    /// <summary>Seconds before the first round expires; each further round doubles it. Besu default 1.</summary>
    public int RequestTimeoutSeconds { get; set; } = QbftChainSpecEngineParameters.DefaultRequestTimeoutSeconds;

    public int GossipedHistoryLimit { get; set; } = QbftChainSpecEngineParameters.DefaultGossipedHistoryLimit;
    public int MessageQueueLimit { get; set; } = QbftChainSpecEngineParameters.DefaultMessageQueueLimit;
    public int DuplicateMessageLimit { get; set; } = QbftChainSpecEngineParameters.DefaultDuplicateMessageLimit;
    public int FutureMessagesLimit { get; set; } = QbftChainSpecEngineParameters.DefaultFutureMessagesLimit;
    public int FutureMessagesMaxDistance { get; set; } = QbftChainSpecEngineParameters.DefaultFutureMessagesMaxDistance;

    /// <summary>Account credited with the block reward and the fees; the proposer when unset.</summary>
    public Address? MiningBeneficiary { get; set; }

    /// <summary>Wei paid to the beneficiary per block. Besu default 0.</summary>
    public UInt256 BlockReward { get; set; }

    /// <summary>Per-transaction gas cap; 0 or unset leaves transactions bounded only by the block gas limit.</summary>
    public ulong? PerTxGasLimit { get; set; }

    /// <summary>Per-fork overrides, Besu's <c>transitions.ibft2</c>.</summary>
    public List<BftTransition> Transitions { get; set; } = [];

    public void ApplyToChainSpec(ChainSpec chainSpec)
    {
        // BFT extra data carries the validator list and every committed seal, far beyond the 32-byte default.
        chainSpec.Parameters.MaximumExtraDataSize = int.MaxValue;

        if (chainSpec.Genesis is { } genesis)
        {
            ValidatePerTxGasLimit(PerTxGasLimit, genesis.GasLimit);
            foreach (BftTransition transition in Transitions)
            {
                ValidatePerTxGasLimit(transition.PerTxGasLimit, genesis.GasLimit);
            }
        }
    }

    public void AddTransitions(SortedSet<ulong> blockNumbers, SortedSet<ulong> timestamps)
    {
        foreach (BftTransition transition in Transitions)
        {
            blockNumbers.Add(transition.Block);
        }
    }

    public void ApplyToReleaseSpec(ReleaseSpec spec, ulong startBlock, ulong? startTimestamp)
    {
        spec.MaximumExtraDataSize = int.MaxValue;
        spec.BlockReward = BftForksSchedule.Create(this, ISpecProvider.TimestampForkNever).GetFork((long)startBlock, startTimestamp ?? 0).BlockReward;
    }

    private static void ValidatePerTxGasLimit(ulong? perTxGasLimit, ulong genesisGasLimit)
    {
        if (perTxGasLimit > genesisGasLimit)
        {
            throw new InvalidOperationException($"pertxgaslimit {perTxGasLimit} exceeds the genesis gas limit {genesisGasLimit}");
        }
    }
}
