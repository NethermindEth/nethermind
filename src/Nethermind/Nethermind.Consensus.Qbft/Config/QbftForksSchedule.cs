// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Consensus.Qbft.Config;

/// <summary>The QBFT options in force from a given block; the resolved form of the genesis options plus applied transitions.</summary>
public sealed record QbftConfigSnapshot
{
    public required long EpochLength { get; init; }
    public required int BlockPeriodSeconds { get; init; }
    public required int EmptyBlockPeriodSeconds { get; init; }
    public required long XBlockPeriodMilliseconds { get; init; }
    public required int RequestTimeoutSeconds { get; init; }
    public required int GossipedHistoryLimit { get; init; }
    public required int MessageQueueLimit { get; init; }
    public required int DuplicateMessageLimit { get; init; }
    public required int FutureMessagesLimit { get; init; }
    public required int FutureMessagesMaxDistance { get; init; }
    public Address? MiningBeneficiary { get; init; }
    public UInt256 BlockReward { get; init; }
    public ulong? PerTxGasLimit { get; init; }
    public Address? ValidatorContractAddress { get; init; }

    public bool IsValidatorContractMode => ValidatorContractAddress is not null;

    /// <summary>The minimum time between blocks, honouring the test-only millisecond period.</summary>
    public TimeSpan MinimumBlockPeriod =>
        XBlockPeriodMilliseconds > 0 ? TimeSpan.FromMilliseconds(XBlockPeriodMilliseconds) : TimeSpan.FromSeconds(BlockPeriodSeconds);
}

/// <summary>A fork activation: the options that apply from <see cref="Block"/> on.</summary>
/// <remarks>
/// Besu types a transition by the milestone era its block falls into: a transition whose block
/// value is at or above the first timestamp-scheduled milestone is compared against block
/// timestamps rather than numbers (<c>ForksSchedule.applyMilestoneTypes</c>).
/// </remarks>
public sealed record QbftForkSpec(ulong Block, bool IsTimestamp, QbftConfigSnapshot Value, IReadOnlyList<Address>? ValidatorOverride);

/// <summary>Resolves which QBFT options apply at a height, mirroring Besu's <c>ForksSchedule&lt;QbftConfigOptions&gt;</c>.</summary>
public sealed class QbftForksSchedule
{
    private readonly QbftForkSpec[] _forksDescending;

    public QbftForksSchedule(IReadOnlyList<QbftForkSpec> forks)
    {
        if (forks.Count == 0) throw new ArgumentException("At least the genesis fork is required.", nameof(forks));
        QbftForkSpec[] sorted = [.. forks];
        Array.Sort(sorted, static (a, b) => b.Block.CompareTo(a.Block));
        _forksDescending = sorted;
    }

    /// <summary>Forks in ascending block order.</summary>
    public IEnumerable<QbftForkSpec> Forks
    {
        get
        {
            for (int i = _forksDescending.Length - 1; i >= 0; i--)
            {
                yield return _forksDescending[i];
            }
        }
    }

    public QbftForkSpec GetForkSpec(long blockNumber, ulong blockTimestamp)
    {
        foreach (QbftForkSpec fork in _forksDescending)
        {
            ulong value = fork.IsTimestamp ? blockTimestamp : (ulong)blockNumber;
            if (value >= fork.Block)
            {
                return fork;
            }
        }

        return _forksDescending[^1];
    }

    public QbftConfigSnapshot GetFork(long blockNumber, ulong blockTimestamp) => GetForkSpec(blockNumber, blockTimestamp).Value;

    /// <summary>The validator list a transition installs at <paramref name="blockNumber"/>, if any.</summary>
    public IReadOnlyList<Address>? GetValidatorOverride(ulong blockNumber)
    {
        foreach (QbftForkSpec fork in _forksDescending)
        {
            if (fork.Block == blockNumber && !fork.IsTimestamp)
            {
                return fork.ValidatorOverride;
            }
        }

        return null;
    }

    /// <param name="firstTimestampFork">The chain's earliest timestamp-scheduled milestone, or <see cref="ISpecProvider.TimestampForkNever"/>.</param>
    public static QbftForksSchedule Create(QbftChainSpecEngineParameters parameters, ulong firstTimestampFork)
    {
        QbftConfigSnapshot genesis = new()
        {
            EpochLength = parameters.EpochLength,
            BlockPeriodSeconds = parameters.BlockPeriodSeconds,
            EmptyBlockPeriodSeconds = parameters.EmptyBlockPeriodSeconds,
            XBlockPeriodMilliseconds = parameters.XBlockPeriodMilliseconds,
            RequestTimeoutSeconds = parameters.RequestTimeoutSeconds,
            GossipedHistoryLimit = parameters.GossipedHistoryLimit,
            MessageQueueLimit = parameters.MessageQueueLimit,
            DuplicateMessageLimit = parameters.DuplicateMessageLimit,
            FutureMessagesLimit = parameters.FutureMessagesLimit,
            FutureMessagesMaxDistance = parameters.FutureMessagesMaxDistance,
            MiningBeneficiary = parameters.MiningBeneficiary,
            BlockReward = parameters.BlockReward,
            PerTxGasLimit = parameters.PerTxGasLimit,
            ValidatorContractAddress = parameters.ValidatorContractAddress,
        };

        List<QbftForkSpec> forks = [new QbftForkSpec(0, false, genesis, null)];
        if (parameters.StartBlock is > 0 and { } startBlock)
        {
            // Below startblock the chain ran IBFT 2.0 with its own period and epoch; QBFT settings apply from startblock on.
            Ibft2Parameters ibft2 = parameters.Ibft2 ?? new Ibft2Parameters();
            forks[0] = new QbftForkSpec(0, false, genesis with
            {
                EpochLength = ibft2.EpochLength,
                BlockPeriodSeconds = ibft2.BlockPeriodSeconds,
                RequestTimeoutSeconds = ibft2.RequestTimeoutSeconds,
            }, null);
            forks.Add(new QbftForkSpec(startBlock, startBlock >= firstTimestampFork, genesis, null));
        }

        QbftTransition[] transitions = [.. parameters.Transitions];
        Array.Sort(transitions, static (a, b) => a.Block.CompareTo(b.Block));
        HashSet<ulong> seen = [];
        foreach (QbftTransition transition in transitions)
        {
            if (transition.Block == 0) throw new ArgumentException("Transition cannot be created for genesis block");
            if (!seen.Add(transition.Block)) throw new ArgumentException("Duplicate transitions cannot be created for the same block");

            QbftConfigSnapshot previous = forks[^1].Value;
            forks.Add(new QbftForkSpec(transition.Block, transition.Block >= firstTimestampFork, Apply(previous, transition), transition.Validators));
        }

        return new QbftForksSchedule(forks);
    }

    private static QbftConfigSnapshot Apply(QbftConfigSnapshot previous, QbftTransition transition)
    {
        Address? contractAddress = previous.ValidatorContractAddress;
        if (transition.ValidatorSelectionMode is { } mode)
        {
            if (mode.Equals("blockheader", StringComparison.OrdinalIgnoreCase))
            {
                if (transition.Validators is null or { Length: 0 })
                {
                    throw new InvalidOperationException("QBFT transition to blockheader mode requires a validators list containing at least one validator");
                }

                contractAddress = null;
            }
            else if (mode.Equals("contract", StringComparison.OrdinalIgnoreCase) && transition.ValidatorContractAddress is not null)
            {
                contractAddress = transition.ValidatorContractAddress;
            }
            else if (transition.ValidatorContractAddress is null)
            {
                throw new InvalidOperationException("QBFT transition has config with contract mode but no contract address");
            }
        }

        return previous with
        {
            BlockPeriodSeconds = transition.BlockPeriodSeconds ?? previous.BlockPeriodSeconds,
            EmptyBlockPeriodSeconds = transition.EmptyBlockPeriodSeconds ?? previous.EmptyBlockPeriodSeconds,
            XBlockPeriodMilliseconds = transition.XBlockPeriodMilliseconds ?? previous.XBlockPeriodMilliseconds,
            BlockReward = transition.BlockReward ?? previous.BlockReward,
            PerTxGasLimit = transition.PerTxGasLimit ?? previous.PerTxGasLimit,
            MiningBeneficiary = transition.MiningBeneficiary is null
                ? previous.MiningBeneficiary
                : string.IsNullOrWhiteSpace(transition.MiningBeneficiary) ? null : new Address(transition.MiningBeneficiary.Trim()),
            ValidatorContractAddress = contractAddress,
        };
    }
}
