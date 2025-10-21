// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Xdc.Spec;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test;
internal class XdcBlockProducerTest
{
    [Test]
    public async Task SampleTest()
    {
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IXdcReleaseSpec xdcReleaseSpec = Substitute.For<IXdcReleaseSpec>();
        xdcReleaseSpec.MinePeriod.Returns(2);
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcReleaseSpec);

        var epochManager = Substitute.For<IEpochSwitchManager>();
        ISealer sealer = Substitute.For<ISealer>();
        sealer.CanSeal(Arg.Any<long>(), Arg.Any<Hash256>()).Returns(true);
        sealer.Address.Returns(TestItem.AddressA);

        IWorldState stateProvider = Substitute.For<IWorldState>();
        stateProvider.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);

        PrivateKey[] masterNodes = XdcTestHelper.GeneratePrivateKeys(108);
        epochManager
            .GetEpochSwitchInfo(Arg.Any<XdcBlockHeader>(), Arg.Any<Hash256>())
            .Returns(new Types.EpochSwitchInfo(masterNodes.Select(m=>m.Address).ToArray(), [], new Types.BlockRoundInfo(Hash256.Zero, 0, 0)));

        XdcBlockHeader parent = Build.A.XdcBlockHeader().TestObject;

        var xdcContext = new XdcContext();
        xdcContext.CurrentRound = 1;
        xdcContext.HighestQC = XdcTestHelper.CreateQc(new Types.BlockRoundInfo(parent.Hash!, 0, parent.Number), 0, masterNodes);

        IBlockchainProcessor processor = Substitute.For<IBlockchainProcessor>();
        processor.Process(Arg.Any<Block>(), Arg.Any<ProcessingOptions>(), Arg.Any<IBlockTracer>()).Returns(args => args.ArgAt<Block>(0));

        XdcBlockProducer producer = new XdcBlockProducer(
            epochManager,
            Substitute.For<ISnapshotManager>(),
            xdcContext,
            Substitute.For<ITxSource>(),
            processor,
            sealer,
            Substitute.For<IBlockTree>(),
            stateProvider,
            Substitute.For<IGasLimitCalculator>(),
            Substitute.For<ITimestamper>(),
            specProvider,
            Substitute.For<ILogManager>(),
            Substitute.For<IDifficultyCalculator>(),
            Substitute.For<IBlocksConfig>());

        Block? block = await producer.BuildBlock(parent);
    }
}
