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
    private static readonly ForkActivation _osakaActivation = new(25_532_382, MainnetSpecProvider.OsakaBlockTimestamp);

    [Test]
    public void Rejects_a_fork_the_chain_schedule_has_left([Values(ProtocolFork.Cancun, ProtocolFork.Prague)] ProtocolFork fork) =>
        Assert.That(() => StatelessSpecProvider.Create(BlockchainIds.Mainnet, fork, _osakaActivation),
            Throws.TypeOf<InvalidDataException>());

    /// <summary>A fork the chain has not scheduled yet is how devnets and spec fixtures pin future rules.</summary>
    [Test]
    public void Accepts_the_scheduled_fork_and_later_ones(
        [Values(ProtocolFork.Current, ProtocolFork.Osaka, ProtocolFork.BPO1, ProtocolFork.Amsterdam)] ProtocolFork fork) =>
        Assert.That(() => StatelessSpecProvider.Create(BlockchainIds.Mainnet, fork, _osakaActivation), Throws.Nothing);
}
