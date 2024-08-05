// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Shutter;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Threading.Tasks;

namespace Nethermind.Merge.AuRa.Test.Shutter;
[TestFixture]
public class ShutterBlockImprovementContextTests
{
    [Test]
    public async Task Test()
    {
        ShutterConfig shutterConfig = new()
        {
            MaxKeyDelay = 1666
        };

        Consensus.Producers.PayloadAttributes payloadAttributes = new()
        {
            Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 5
        };

        ShutterBlockImprovementContext improvementContext = new(
            Substitute.For<IBlockProducer>(),
            Substitute.For<IShutterTxSignal>(),
            shutterConfig,
            Build.A.Block.TestObject,
            Build.A.BlockHeader.TestObject,
            payloadAttributes,
            DateTimeOffset.UtcNow,
            GnosisSpecProvider.BeaconChainGenesisTimestamp * 1000,
            TimeSpan.FromSeconds(5),
            LimboLogs.Instance);

        await improvementContext.ImprovementTask;
    }

}
