// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using System.Collections.Generic;
using System;
using NUnit.Framework;
using NSubstitute;
using Nethermind.State;
using Nethermind.Optimism.Rpc;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Logging;
using Nethermind.Int256;
using Nethermind.Evm.Tracing;
using Nethermind.Core.Timers;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Specs;
using Nethermind.Core.Crypto;
using Nethermind.Core;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus;
using Nethermind.Config;
using Nethermind.Blockchain;
using FluentAssertions;
using Nethermind.Crypto;
using System.Threading;

namespace Nethermind.Optimism.Test;

[Parallelizable(ParallelScope.All)]
public class OptimismPayloadPreparationServiceTests
{
    private static IEnumerable<(OptimismPayloadAttributes, EIP1559Parameters?)> TestCases()
    {
        foreach (var noTxPool in (bool[])[true, false])
        {
            yield return (new OptimismPayloadAttributes { EIP1559Params = [0, 0, 0, 8, 0, 0, 0, 2], NoTxPool = noTxPool }, new EIP1559Parameters(0, 8, 2));
            yield return (new OptimismPayloadAttributes { EIP1559Params = [0, 0, 0, 2, 0, 0, 0, 2], NoTxPool = noTxPool }, new EIP1559Parameters(0, 2, 2));
            yield return (new OptimismPayloadAttributes { EIP1559Params = [0, 0, 0, 2, 0, 0, 0, 10], NoTxPool = noTxPool }, new EIP1559Parameters(0, 2, 10));
            yield return (new OptimismPayloadAttributes { EIP1559Params = [0, 0, 0, 0, 0, 0, 0, 0], NoTxPool = noTxPool }, new EIP1559Parameters(0, 250, 6));
        }
    }
    [TestCaseSource(nameof(TestCases))]
    public async Task Writes_EIP1559Params_Into_HeaderExtraData((OptimismPayloadAttributes Attributes, EIP1559Parameters? ExpectedEIP1559Parameters) testCase)
    {
        var parent = Build.A.BlockHeader.TestObject;

        var releaseSpec = Substitute.For<IReleaseSpec>();
        releaseSpec.IsOpHoloceneEnabled.Returns(true);
        releaseSpec.BaseFeeMaxChangeDenominator.Returns((UInt256)250);
        releaseSpec.ElasticityMultiplier.Returns(6);
        var specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(parent).Returns(releaseSpec);

        var stateProvider = Substitute.For<IWorldState>();
        stateProvider.HasStateForRoot(Arg.Any<Hash256>()).Returns(true);

        var block = Build.A.Block
            .WithExtraData([])
            .TestObject;
        IBlockchainProcessor processor = Substitute.For<IBlockchainProcessor>();
        processor.Process(Arg.Any<Block>(), ProcessingOptions.ProducingBlock, Arg.Any<IBlockTracer>(), Arg.Any<CancellationToken>()).Returns(block);

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

        testCase.Attributes.PrevRandao = Hash256.Zero;
        testCase.Attributes.SuggestedFeeRecipient = TestItem.AddressA;

        var payloadId = service.StartPreparingPayload(parent, testCase.Attributes);
        var context = await service.GetPayload(payloadId);
        var currentBestBlock = context?.CurrentBestBlock!;

        currentBestBlock.Should().Be(block);
        currentBestBlock.Header.TryDecodeEIP1559Parameters(out var parameters, out _).Should().BeTrue();
        parameters.Should().BeEquivalentTo(testCase.ExpectedEIP1559Parameters);
        currentBestBlock.Header.Hash.Should().BeEquivalentTo(currentBestBlock.Header.CalculateHash());
    }
}
