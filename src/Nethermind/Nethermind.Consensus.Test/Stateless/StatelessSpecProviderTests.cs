// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Specs;
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

    /// <summary>
    /// A fork the chain has not scheduled yet is how devnets and spec fixtures pin future rules, including at
    /// the far-future placeholder activation mainnet parks its unscheduled forks at.
    /// </summary>
    [Test]
    public void Accepts_the_scheduled_fork_and_later_ones(
        [Values(ProtocolFork.Current, ProtocolFork.Osaka, ProtocolFork.BPO1, ProtocolFork.Amsterdam)] ProtocolFork fork,
        [Values] bool placeholderActivation) =>
        Assert.That(() => StatelessSpecProvider.Create(BlockchainIds.Mainnet, fork,
            placeholderActivation ? MainnetSpecProvider.BogotaActivation : MainnetSpecProvider.OsakaActivation), Throws.Nothing);

    private static ForkActivation GetOsakaActivation(ulong chainId) =>
        chainId == BlockchainIds.Gnosis ? _gnosisOsakaActivation : MainnetSpecProvider.OsakaActivation;
}
