// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using NUnit.Framework;
using FluentAssertions;
using System;
using NSubstitute;
using Nethermind.Core.Specs;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Optimism.Rpc;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Processing;
using Nethermind.Blockchain;
using Nethermind.State;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using System.Buffers.Binary;
using System.Threading.Tasks;

namespace Nethermind.Optimism.Test;

[Parallelizable(ParallelScope.All)]
public class OptimismPayloadPreparationServiceTests
{
    [TestCase(8u, 2u)]
    [TestCase(2u, 2u)]
    [TestCase(2u, 10u)]
    public async Task Writes_EIP1559Params_Into_HeaderExtraData(UInt32 denominator, UInt32 elasticity)
    {
        var parent = Build.A.BlockHeader.TestObject;

        var releaseSpec = Substitute.For<IReleaseSpec>();
        releaseSpec.IsOpHoloceneEnabled.Returns(true);
        var specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(parent).Returns(releaseSpec);

        var stateProvider = Substitute.For<IWorldState>();
        stateProvider.HasStateForRoot(Arg.Any<Hash256>()).Returns(true);

        var block = Build.A.Block
            .WithExtraData([])
            .TestObject;
        IBlockchainProcessor processor = Substitute.For<IBlockchainProcessor>();
        processor.Process(Arg.Any<Block>(), ProcessingOptions.ProducingBlock, Arg.Any<IBlockTracer>()).Returns(block);

        var service = new OptimismPayloadPreparationService(
            blockProducer: new PostMergeBlockProducer(
                processor: processor,
                specProvider: specProvider,
                stateProvider: stateProvider,
                txSource: Substitute.For<ITxSource>(),
                blockTree: Substitute.For<IBlockTree>(),
                gasLimitCalculator: Substitute.For<IGasLimitCalculator>(),
                sealEngine: Substitute.For<ISealEngine>(),
                timestamper: Substitute.For<ITimestamper>(),
                miningConfig: Substitute.For<IBlocksConfig>(),
                logManager: TestLogManager.Instance
            ),
            specProvider: specProvider,
            blockImprovementContextFactory: NoBlockImprovementContextFactory.Instance,
            timePerSlot: TimeSpan.FromSeconds(1),
            timerFactory: Substitute.For<ITimerFactory>(),
            logManager: TestLogManager.Instance
        );

        var eip1559Params = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(eip1559Params.AsSpan(0, 4), denominator);
        BinaryPrimitives.WriteUInt32BigEndian(eip1559Params.AsSpan(4, 4), elasticity);

        var attributes = new OptimismPayloadAttributes()
        {
            PrevRandao = Hash256.Zero,
            SuggestedFeeRecipient = TestItem.AddressA,
            EIP1559Params = eip1559Params,
        };

        var payloadId = service.StartPreparingPayload(parent, attributes);
        var context = await service.GetPayload(payloadId);
        var currentBestBlock = context?.CurrentBestBlock!;

        currentBestBlock.Should().Be(block);
        currentBestBlock.Header.TryDecodeEIP1559Parameters(out var parameters, out _).Should().BeTrue();
        parameters.Should().BeEquivalentTo(new EIP1559Parameters(0, denominator, elasticity));
    }
}
