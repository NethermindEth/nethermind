// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.Qbft.Config;

/// <summary>
/// The <c>qbft</c> engine section of a chainspec: Besu's genesis <c>config.qbft</c> options plus the
/// <c>config.transitions.qbft</c> array (as <see cref="Transitions"/>) and, for chains migrated from
/// IBFT 2.0, the <c>config.ibft2</c> section (as <see cref="Ibft2"/>).
/// </summary>
/// <remarks>
/// Keys are matched case-insensitively, so Besu's lowercase names (<c>blockperiodseconds</c>) and
/// camelCase both work. Defaults follow Besu's <c>JsonBftConfigOptions</c>.
/// </remarks>
public class QbftChainSpecEngineParameters : IChainSpecEngineParameters
{
    public const string Engine = "qbft";
    public const long DefaultEpochLength = 30_000;
    public const int DefaultBlockPeriodSeconds = 1;
    public const int DefaultRequestTimeoutSeconds = 1;
    public const int DefaultGossipedHistoryLimit = 1000;
    public const int DefaultMessageQueueLimit = 1000;
    public const int DefaultDuplicateMessageLimit = 100;
    public const int DefaultFutureMessagesLimit = 1000;
    public const int DefaultFutureMessagesMaxDistance = 10;

    public string EngineName => Engine;
    public string SealEngineType => Core.SealEngineType.Qbft;

    /// <summary>Blocks between vote resets. Besu key <c>epochlength</c>.</summary>
    public long EpochLength { get; set; } = DefaultEpochLength;

    /// <summary>Minimum seconds between blocks. Besu key <c>blockperiodseconds</c>.</summary>
    public int BlockPeriodSeconds { get; set; } = DefaultBlockPeriodSeconds;

    /// <summary>Seconds to wait before proposing an empty block; 0 disables the wait. Besu key <c>emptyblockperiodseconds</c>.</summary>
    public int EmptyBlockPeriodSeconds { get; set; }

    /// <summary>Test-only sub-second block period; when positive it replaces <see cref="BlockPeriodSeconds"/>. Besu key <c>xblockperiodmilliseconds</c>.</summary>
    public long XBlockPeriodMilliseconds { get; set; }

    /// <summary>Base round timeout in seconds, doubled every round. Besu key <c>requesttimeoutseconds</c>.</summary>
    public int RequestTimeoutSeconds { get; set; } = DefaultRequestTimeoutSeconds;

    public int GossipedHistoryLimit { get; set; } = DefaultGossipedHistoryLimit;
    public int MessageQueueLimit { get; set; } = DefaultMessageQueueLimit;
    public int DuplicateMessageLimit { get; set; } = DefaultDuplicateMessageLimit;
    public int FutureMessagesLimit { get; set; } = DefaultFutureMessagesLimit;
    public int FutureMessagesMaxDistance { get; set; } = DefaultFutureMessagesMaxDistance;

    /// <summary>Recipient of block rewards and fees instead of the proposer. Besu key <c>miningbeneficiary</c>.</summary>
    public Address? MiningBeneficiary { get; set; }

    /// <summary>Reward paid per block in wei. Besu key <c>blockreward</c>.</summary>
    public UInt256 BlockReward { get; set; }

    /// <summary>Per-transaction gas limit cap; 0 means unlimited. Besu key <c>pertxgaslimit</c>.</summary>
    public ulong? PerTxGasLimit { get; set; }

    /// <summary>When set, validators come from this contract's <c>getValidators()</c> instead of block headers. Besu key <c>validatorcontractaddress</c>.</summary>
    public Address? ValidatorContractAddress { get; set; }

    /// <summary>First QBFT block of a chain migrated from IBFT 2.0. Besu key <c>startblock</c>.</summary>
    public ulong? StartBlock { get; set; }

    /// <summary>Block-scheduled changes, Besu's <c>transitions.qbft</c>.</summary>
    public List<BftTransition> Transitions { get; set; } = [];

    /// <summary>The pre-migration IBFT 2.0 options of a migrated chain, Besu's <c>config.ibft2</c>.</summary>
    public Ibft2Parameters? Ibft2 { get; set; }

    [JsonIgnore]
    public bool IsValidatorContractMode => ValidatorContractAddress is not null;

    [JsonIgnore]
    public bool IsMigratedFromIbft2 => StartBlock is > 0;

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

    private static void ValidatePerTxGasLimit(ulong? perTxGasLimit, ulong genesisGasLimit)
    {
        if (perTxGasLimit > genesisGasLimit)
        {
            throw new InvalidOperationException($"pertxgaslimit {perTxGasLimit} exceeds the genesis gas limit {genesisGasLimit}");
        }
    }

    public void AddTransitions(SortedSet<ulong> blockNumbers, SortedSet<ulong> timestamps)
    {
        foreach (BftTransition transition in Transitions)
        {
            blockNumbers.Add(transition.Block);
        }

        if (StartBlock is { } startBlock)
        {
            blockNumbers.Add(startBlock);
        }
    }

    public void ApplyToReleaseSpec(ReleaseSpec spec, ulong startBlock, ulong? startTimestamp)
    {
        spec.MaximumExtraDataSize = int.MaxValue;
        spec.BlockReward = BftForksSchedule.Create(this, ISpecProvider.TimestampForkNever).GetFork((long)startBlock, startTimestamp ?? 0).BlockReward;
    }
}

/// <summary>One entry of Besu's <c>transitions.qbft</c>; unset members keep the previous fork's value.</summary>
public class BftTransition
{
    public ulong Block { get; set; }
    public int? BlockPeriodSeconds { get; set; }
    public int? EmptyBlockPeriodSeconds { get; set; }
    public long? XBlockPeriodMilliseconds { get; set; }
    public UInt256? BlockReward { get; set; }

    /// <summary>Null means "not configured"; an empty string clears the beneficiary so fees go back to the proposer.</summary>
    public string? MiningBeneficiary { get; set; }

    public ulong? PerTxGasLimit { get; set; }

    /// <summary><c>blockheader</c> or <c>contract</c>.</summary>
    public string? ValidatorSelectionMode { get; set; }

    public Address? ValidatorContractAddress { get; set; }

    /// <summary>Validator set that replaces the tallied one from <see cref="Block"/> on (block header mode only).</summary>
    public Address[]? Validators { get; set; }
}

/// <summary>The IBFT 2.0 options that still matter for validating pre-migration blocks.</summary>
public class Ibft2Parameters
{
    public long EpochLength { get; set; } = QbftChainSpecEngineParameters.DefaultEpochLength;
    public int BlockPeriodSeconds { get; set; } = QbftChainSpecEngineParameters.DefaultBlockPeriodSeconds;
    public int RequestTimeoutSeconds { get; set; } = QbftChainSpecEngineParameters.DefaultRequestTimeoutSeconds;
}
