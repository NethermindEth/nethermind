// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Shutter.Config;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Threading.Tasks;

namespace Nethermind.Shutter.Test;

[TestFixture]
public class ShutterBlockImprovementContextTests
{
    // [Test]
    // public async Task Test()
    // {
    //     ShutterConfig cfg = new()
    //     {
    //         MaxKeyDelay = 1666
    //     };

    //     PayloadAttributes payloadAttributes = new()
    //     {
    //         Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 5
    //     };

    //     ShutterBlockImprovementContextFactory improvementContextFactory = new(
    //         Substitute.For<IBlockProducer>(),
    //         Substitute.For<ShutterTxSource>(),
    //         cfg,
    //         GnosisSpecProvider.Instance,
    //         LimboLogs.Instance
    //     );

    //     IBlockImprovementContext improvementContext = improvementContextFactory.StartBlockImprovementContext(
    //         Build.A.Block.TestObject,
    //         Build.A.BlockHeader.TestObject,
    //         payloadAttributes,
    //         DateTimeOffset.UtcNow
    //     );

    //     await improvementContext.ImprovementTask;
    // }

}
