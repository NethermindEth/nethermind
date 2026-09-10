// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Qbft.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Consensus.Qbft.Test.Config;

/// <summary>Port of Besu's <c>ForksScheduleTest</c> and <c>QbftForksSchedulesFactoryTest</c>.</summary>
[Parallelizable(ParallelScope.All)]
public class QbftForksScheduleTests
{
    private static readonly Address Beneficiary = new("0xdee0519f7c7cb0f9843fa1e93b99255c89507a9c");

    private static QbftChainSpecEngineParameters Parameters(params QbftTransition[] transitions) => new() { Transitions = [.. transitions] };

    private static QbftForksSchedule Create(QbftChainSpecEngineParameters parameters, ulong firstTimestampFork = ISpecProvider.TimestampForkNever) =>
        QbftForksSchedule.Create(parameters, firstTimestampFork);

    [Test]
    public void RetrievesGenesisFork()
    {
        QbftForksSchedule schedule = Create(Parameters(new QbftTransition { Block = 10, BlockPeriodSeconds = 10 }));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(schedule.GetForkSpec(0, 0).Block, Is.EqualTo(0));
            Assert.That(schedule.GetForkSpec(1, 0).Block, Is.EqualTo(0));
            Assert.That(schedule.GetFork(1, 0).BlockPeriodSeconds, Is.EqualTo(QbftChainSpecEngineParameters.DefaultBlockPeriodSeconds));
        }
    }

    [Test]
    public void RetrievesLatestForkByBlockNumber()
    {
        QbftForksSchedule schedule = Create(Parameters(
            new QbftTransition { Block = 1, BlockPeriodSeconds = 10 },
            new QbftTransition { Block = 2, BlockPeriodSeconds = 20 },
            new QbftTransition { Block = 3, MiningBeneficiary = Beneficiary.ToString() },
            new QbftTransition { Block = 4, MiningBeneficiary = "" }));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(schedule.GetForkSpec(0, 0).Block, Is.EqualTo(0));
            Assert.That(schedule.GetFork(1, 0).BlockPeriodSeconds, Is.EqualTo(10));
            Assert.That(schedule.GetFork(2, 0).BlockPeriodSeconds, Is.EqualTo(20));
            Assert.That(schedule.GetFork(3, 0).BlockPeriodSeconds, Is.EqualTo(20), "unset values carry over");
            Assert.That(schedule.GetFork(3, 0).MiningBeneficiary, Is.EqualTo(Beneficiary));
            Assert.That(schedule.GetFork(4, 0).MiningBeneficiary, Is.Null, "an empty beneficiary clears it");
            Assert.That(schedule.GetFork(100, 0).MiningBeneficiary, Is.Null);
        }
    }

    [Test]
    public void RetrievesLatestForkByTimestampOnceTimestampMilestonesStart()
    {
        // Every transition at or above the first timestamp milestone (100) is compared against block timestamps.
        QbftForksSchedule schedule = Create(Parameters(
            new QbftTransition { Block = 100, BlockPeriodSeconds = 10 },
            new QbftTransition { Block = 200, BlockPeriodSeconds = 20 },
            new QbftTransition { Block = 300, MiningBeneficiary = Beneficiary.ToString() },
            new QbftTransition { Block = 400, MiningBeneficiary = "" }), firstTimestampFork: 100);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(schedule.GetForkSpec(0, 0).Block, Is.EqualTo(0));
            Assert.That(schedule.GetFork(0, 100).BlockPeriodSeconds, Is.EqualTo(10));
            Assert.That(schedule.GetFork(0, 200).BlockPeriodSeconds, Is.EqualTo(20));
            Assert.That(schedule.GetFork(0, 300).MiningBeneficiary, Is.EqualTo(Beneficiary));
            Assert.That(schedule.GetFork(0, 400).MiningBeneficiary, Is.Null);
            Assert.That(schedule.GetFork(1000, 0).BlockPeriodSeconds, Is.EqualTo(QbftChainSpecEngineParameters.DefaultBlockPeriodSeconds), "block number no longer selects timestamp forks");
        }
    }

    [Test]
    public void FallbackReturnsSmallestForkNotLargest()
    {
        QbftForksSchedule schedule = new([
            new QbftForkSpec(20, false, Snapshot(20), null),
            new QbftForkSpec(10, false, Snapshot(10), null),
        ]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(schedule.GetFork(5, 5).BlockPeriodSeconds, Is.EqualTo(10));
            Assert.That(schedule.GetFork(0, 0).BlockPeriodSeconds, Is.EqualTo(10));
        }
    }

    [Test]
    public void CreatesScheduleWithForkThatOverridesGenesisValues()
    {
        QbftForksSchedule schedule = Create(Parameters(new QbftTransition
        {
            Block = 1,
            Validators = [QbftTestData.Addr(1), QbftTestData.Addr(2), QbftTestData.Addr(3)],
            BlockPeriodSeconds = 10,
            EmptyBlockPeriodSeconds = 60,
            BlockReward = 5,
            ValidatorSelectionMode = "contract",
            ValidatorContractAddress = QbftTestData.Addr(0x10),
        }));

        QbftConfigSnapshot genesis = schedule.GetFork(0, 0);
        QbftConfigSnapshot fork = schedule.GetFork(1, 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(genesis.BlockPeriodSeconds, Is.EqualTo(1));
            Assert.That(genesis.IsValidatorContractMode, Is.False);
            Assert.That(fork.BlockPeriodSeconds, Is.EqualTo(10));
            Assert.That(fork.EmptyBlockPeriodSeconds, Is.EqualTo(60));
            Assert.That(fork.BlockReward, Is.EqualTo((UInt256)5));
            Assert.That(fork.ValidatorContractAddress, Is.EqualTo(QbftTestData.Addr(0x10)));
            Assert.That(schedule.GetFork(2, 0), Is.EqualTo(fork));
            Assert.That(schedule.GetValidatorOverride(1), Is.EqualTo(new[] { QbftTestData.Addr(1), QbftTestData.Addr(2), QbftTestData.Addr(3) }));
            Assert.That(schedule.GetValidatorOverride(2), Is.Null);
        }
    }

    [Test]
    public void CreatingScheduleThrowsErrorForContractForkWithoutContractAddress() =>
        Assert.That(
            () => Create(Parameters(new QbftTransition { Block = 1, ValidatorSelectionMode = "contract" })),
            Throws.InvalidOperationException.With.Message.EqualTo("QBFT transition has config with contract mode but no contract address"));

    [Test]
    public void SwitchingToBlockHeaderRemovesValidatorContractAddress()
    {
        QbftChainSpecEngineParameters parameters = Parameters(new QbftTransition { Block = 1, ValidatorSelectionMode = "blockheader", Validators = [QbftTestData.Addr(1)] });
        parameters.ValidatorContractAddress = QbftTestData.Addr(0x10);
        QbftForksSchedule schedule = Create(parameters);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(schedule.GetFork(0, 0).IsValidatorContractMode, Is.True);
            Assert.That(schedule.GetFork(1, 0).ValidatorContractAddress, Is.Null);
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public void SwitchingToBlockHeaderRequiresValidators(bool validatorsListIsNull)
    {
        QbftChainSpecEngineParameters parameters = Parameters(new QbftTransition
        {
            Block = 1,
            ValidatorSelectionMode = "blockheader",
            Validators = validatorsListIsNull ? null : [],
        });
        parameters.ValidatorContractAddress = QbftTestData.Addr(0x10);
        Assert.That(
            () => Create(parameters),
            Throws.InvalidOperationException.With.Message.EqualTo("QBFT transition to blockheader mode requires a validators list containing at least one validator"));
    }

    [Test]
    public void TransactionGasLimitIsPreservedAndChangedAcrossTransitions()
    {
        QbftChainSpecEngineParameters parameters = Parameters(
            new QbftTransition { Block = 1, BlockPeriodSeconds = 10 },
            new QbftTransition { Block = 5, PerTxGasLimit = 16_000_000 });
        parameters.PerTxGasLimit = 8_000_000;
        QbftForksSchedule schedule = Create(parameters);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(schedule.GetFork(0, 0).PerTxGasLimit, Is.EqualTo(8_000_000UL));
            Assert.That(schedule.GetFork(1, 0).PerTxGasLimit, Is.EqualTo(8_000_000UL));
            Assert.That(schedule.GetFork(4, 0).PerTxGasLimit, Is.EqualTo(8_000_000UL));
            Assert.That(schedule.GetFork(5, 0).PerTxGasLimit, Is.EqualTo(16_000_000UL));
            Assert.That(schedule.GetFork(6, 0).PerTxGasLimit, Is.EqualTo(16_000_000UL));
        }
    }

    [Test]
    public void MigrationFromIbft2KeepsIbft2SettingsBelowStartBlock()
    {
        QbftChainSpecEngineParameters parameters = new()
        {
            BlockPeriodSeconds = 5,
            EpochLength = 1000,
            StartBlock = 300,
            Ibft2 = new Ibft2Parameters { BlockPeriodSeconds = 2, EpochLength = 100 },
        };
        QbftForksSchedule schedule = Create(parameters);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(schedule.GetFork(0, 0).BlockPeriodSeconds, Is.EqualTo(2));
            Assert.That(schedule.GetFork(299, 0).EpochLength, Is.EqualTo(100));
            Assert.That(schedule.GetFork(300, 0).BlockPeriodSeconds, Is.EqualTo(5));
            Assert.That(schedule.GetFork(300, 0).EpochLength, Is.EqualTo(1000));
        }
    }

    [Test]
    public void TransitionAtGenesisIsRejected() =>
        Assert.That(() => Create(Parameters(new QbftTransition { Block = 0 })), Throws.ArgumentException.With.Message.Contains("genesis"));

    [Test]
    public void DuplicateTransitionBlocksAreRejected() =>
        Assert.That(() => Create(Parameters(new QbftTransition { Block = 3 }, new QbftTransition { Block = 3 })), Throws.ArgumentException.With.Message.Contains("Duplicate"));

    [Test]
    public void ForksAreEnumeratedInAscendingOrder()
    {
        QbftForksSchedule schedule = Create(Parameters(new QbftTransition { Block = 30 }, new QbftTransition { Block = 10 }, new QbftTransition { Block = 20 }));
        List<ulong> blocks = [];
        foreach (QbftForkSpec fork in schedule.Forks) blocks.Add(fork.Block);
        Assert.That(blocks, Is.EqualTo(new ulong[] { 0, 10, 20, 30 }));
    }

    [Test]
    public void MinimumBlockPeriodPrefersTheMillisecondTestPeriod()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Snapshot(4).MinimumBlockPeriod, Is.EqualTo(TimeSpan.FromSeconds(4)));
            Assert.That((Snapshot(4) with { XBlockPeriodMilliseconds = 250 }).MinimumBlockPeriod, Is.EqualTo(TimeSpan.FromMilliseconds(250)));
        }
    }

    private static QbftConfigSnapshot Snapshot(int blockPeriodSeconds) => new()
    {
        EpochLength = QbftChainSpecEngineParameters.DefaultEpochLength,
        BlockPeriodSeconds = blockPeriodSeconds,
        EmptyBlockPeriodSeconds = 0,
        XBlockPeriodMilliseconds = 0,
        RequestTimeoutSeconds = 1,
        GossipedHistoryLimit = 1000,
        MessageQueueLimit = 1000,
        DuplicateMessageLimit = 100,
        FutureMessagesLimit = 1000,
        FutureMessagesMaxDistance = 10,
    };
}
