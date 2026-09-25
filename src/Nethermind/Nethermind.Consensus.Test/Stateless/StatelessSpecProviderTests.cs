// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.Stateless.Execution;
using Nethermind.Stateless.Execution.IO;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Stateless;

public class StatelessSpecProviderTests
{
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
    /// A scheduled fork outside <see cref="ProtocolFork"/> is judged by what the chain ran before it: nothing
    /// pinnable before Cancun, the pinned fork itself or a later one after Amsterdam.
    /// </summary>
    [TestCase(ProtocolFork.Cancun, 5UL, ExpectedResult = false)]
    [TestCase(ProtocolFork.Amsterdam, 20UL, ExpectedResult = false)]
    [TestCase(ProtocolFork.Amsterdam, 30UL, ExpectedResult = true)]
    [TestCase(ProtocolFork.Osaka, 30UL, ExpectedResult = true)]
    public bool Judges_a_scheduled_fork_outside_the_protocol_forks(ProtocolFork fork, ulong timestamp) =>
        StatelessSpecProvider.IsSuperseded(
            new CustomSpecProvider(((0UL, 0UL), Shanghai.Instance), ((0UL, 10UL), Cancun.Instance),
                ((0UL, 20UL), Amsterdam.Instance), ((0UL, 30UL), Bogota.Instance)),
            fork, (0UL, timestamp));

    private static ForkActivation GetOsakaActivation(ulong chainId) =>
        chainId == BlockchainIds.Gnosis ? _gnosisOsakaActivation : MainnetSpecProvider.OsakaActivation;
}
