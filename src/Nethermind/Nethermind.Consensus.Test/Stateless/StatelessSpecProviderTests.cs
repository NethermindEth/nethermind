// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.Stateless.Execution;
using Nethermind.Stateless.Execution.IO;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Stateless;

public class StatelessSpecProviderTests
{
    private const ulong DevnetChainId = 1337;
    private static readonly ForkActivation _gnosisOsakaActivation = (GnosisSpecProvider.LondonBlockNumber, GnosisSpecProvider.OsakaTimestamp);

    [Test]
    public void Rejects_a_fork_the_chain_schedule_has_left(
        [Values(BlockchainIds.Mainnet, BlockchainIds.Gnosis)] ulong chainId,
        [Values(ProtocolFork.Cancun, ProtocolFork.Prague)] ProtocolFork fork) =>
        Assert.That(() => StatelessSpecProvider.Create(chainId, fork, GetOsakaActivation(chainId)),
            Throws.TypeOf<InvalidDataException>());

    /// <summary>A fork the chain has not scheduled yet is how devnets and spec fixtures pin future rules.</summary>
    [Test]
    public void Accepts_the_scheduled_fork_and_later_ones(
        [Values(ProtocolFork.Current, ProtocolFork.Osaka, ProtocolFork.BPO1, ProtocolFork.Amsterdam)] ProtocolFork fork) =>
        Assert.That(() => StatelessSpecProvider.Create(BlockchainIds.Mainnet, fork, MainnetSpecProvider.OsakaActivation), Throws.Nothing);

    /// <summary>
    /// Spec fixtures pin Amsterdam at the far-future placeholder activations mainnet parks its unscheduled forks at.
    /// </summary>
    [Test]
    public void Accepts_amsterdam_at_placeholder_activations(
        [Values(ProtocolFork.Current, ProtocolFork.Amsterdam)] ProtocolFork fork,
        [Values] bool bogota) =>
        Assert.That(() => StatelessSpecProvider.Create(BlockchainIds.Mainnet, fork,
            bogota ? MainnetSpecProvider.BogotaActivation : MainnetSpecProvider.AmsterdamActivation), Throws.Nothing);

    /// <remarks>The guest resolves these chains with its own switch in <c>StatelessSpecProvider.zkevm.cs</c>.</remarks>
    [Test]
    public void Guest_chain_switch_covers_every_built_in_provider() =>
        Assert.That(ChainSpecBasedSpecProvider.KnownProvidersByChainId.Keys, Is.EquivalentTo(new[]
        {
            BlockchainIds.Mainnet, BlockchainIds.Gnosis, BlockchainIds.Chiado,
            BlockchainIds.Sepolia, BlockchainIds.Hoodi, BlockchainIds.Morden
        }));

    /// <summary>
    /// The London fork-block rules (doubled gas limit target, initial base fee) belong to the chain's own London block,
    /// not to mainnet's block number on chains that are post-merge long before reaching it.
    /// </summary>
    [TestCase(BlockchainIds.Sepolia, ProtocolFork.Current, SepoliaSpecProvider.BPO2Timestamp)]
    [TestCase(BlockchainIds.Hoodi, ProtocolFork.Current, HoodiSpecProvider.BPO2Timestamp)]
    [TestCase(BlockchainIds.Chiado, ProtocolFork.Current, ChiadoSpecProvider.OsakaTimestamp)]
    [TestCase(DevnetChainId, ProtocolFork.Amsterdam, MainnetSpecProvider.BPO2BlockTimestamp)]
    public void Does_not_apply_london_fork_block_rules_at_mainnet_london_block(ulong chainId, ProtocolFork fork, ulong timestamp)
    {
        ForkActivation activation = (MainnetSpecProvider.LondonBlockNumber, timestamp);
        IReleaseSpec spec = StatelessSpecProvider.Create(chainId, fork, activation).GetSpec(activation);
        BlockHeader parent = Build.A.BlockHeader
            .WithNumber(MainnetSpecProvider.LondonBlockNumber - 1)
            .WithGasLimit(30_000_000)
            .WithGasUsed(15_000_000)
            .WithBaseFee(7 * Unit.GWei)
            .TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Eip1559GasLimitAdjuster.AdjustGasLimit(spec, parent.GasLimit, MainnetSpecProvider.LondonBlockNumber), Is.EqualTo(parent.GasLimit));
            Assert.That(BaseFeeCalculator.CalculateForReferenceSlot(parent, spec), Is.EqualTo(parent.BaseFeePerGas));
        }
    }

    [Test]
    public void Follows_the_chain_schedule([Values(BlockchainIds.Mainnet, BlockchainIds.Gnosis)] ulong chainId)
    {
        ForkActivation activation = GetOsakaActivation(chainId);
        IReleaseSpec scheduled = ChainSpecBasedSpecProvider.KnownProvidersByChainId[chainId].GetSpec(activation);

        Assert.That(StatelessSpecProvider.Create(chainId, ProtocolFork.Current, activation).GetSpec(activation), Is.SameAs(scheduled));
    }

    [Test]
    public void Pins_the_fork_the_chain_schedules_under_that_name() =>
        Assert.That(
            StatelessSpecProvider.Create(BlockchainIds.Mainnet, ProtocolFork.Amsterdam, MainnetSpecProvider.OsakaActivation)
                .GetSpec(MainnetSpecProvider.OsakaActivation),
            Is.SameAs(Amsterdam.Instance));

    private static ForkActivation GetOsakaActivation(ulong chainId) =>
        chainId == BlockchainIds.Gnosis ? _gnosisOsakaActivation : MainnetSpecProvider.OsakaActivation;
}
