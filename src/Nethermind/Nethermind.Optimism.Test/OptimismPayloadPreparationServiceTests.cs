// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using NUnit.Framework;
using NSubstitute;
using Nethermind.Evm.State;
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
using Nethermind.Crypto;
using System.Threading;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Threading;
using Nethermind.TxPool;

namespace Nethermind.Optimism.Test;

[Parallelizable(ParallelScope.All)]
public class OptimismPayloadPreparationServiceTests
{
    private static IEnumerable<(OptimismPayloadAttributes, EIP1559Parameters?)> TestCases()
    {
        foreach (bool noTxPool in (bool[])[true, false])
        {
            // V0
            yield return (new() { EIP1559Params = [0, 0, 0, 8, 0, 0, 0, 2], NoTxPool = noTxPool }, new EIP1559Parameters(0, 8, 2));
            yield return (new() { EIP1559Params = [0, 0, 0, 2, 0, 0, 0, 2], NoTxPool = noTxPool }, new EIP1559Parameters(0, 2, 2));
            yield return (new() { EIP1559Params = [0, 0, 0, 2, 0, 0, 0, 10], NoTxPool = noTxPool }, new EIP1559Parameters(0, 2, 10));
            yield return (new() { EIP1559Params = [0, 0, 0, 0, 0, 0, 0, 0], NoTxPool = noTxPool }, new EIP1559Parameters(0, 250, 6));

            // V1
            yield return (new() { EIP1559Params = [0, 0, 0, 8, 0, 0, 0, 2], MinBaseFee = 0, NoTxPool = noTxPool }, new EIP1559Parameters(1, 8, 2, 0));
            yield return (new() { EIP1559Params = [0, 0, 0, 2, 0, 0, 0, 2], MinBaseFee = 255, NoTxPool = noTxPool }, new EIP1559Parameters(1, 2, 2, 255));
            yield return (new() { EIP1559Params = [0, 0, 0, 2, 0, 0, 0, 10], MinBaseFee = ulong.MaxValue, NoTxPool = noTxPool }, new EIP1559Parameters(1, 2, 10, ulong.MaxValue));
            yield return (new() { EIP1559Params = [0, 0, 0, 0, 0, 0, 0, 0], MinBaseFee = 0, NoTxPool = noTxPool }, new EIP1559Parameters(1, 250, 6, 0));
            yield return (new() { EIP1559Params = [0, 0, 0, 0, 0, 0, 0, 0], MinBaseFee = 255, NoTxPool = noTxPool }, new EIP1559Parameters(1, 250, 6, 255));
        }
    }
    [TestCaseSource(nameof(TestCases))]
    public async Task Writes_EIP1559Params_Into_HeaderExtraData((OptimismPayloadAttributes Attributes, EIP1559Parameters? ExpectedEIP1559Parameters) testCase)
    {
        BlockHeader parent = Build.A.BlockHeader.TestObject;

        IOptimismReleaseSpec releaseSpec = OptimismReleaseSpecSubstitute.Create();
        releaseSpec.IsOpHoloceneEnabled.Returns(true);
        releaseSpec.BaseFeeMaxChangeDenominator.Returns((UInt256)250);
        releaseSpec.ElasticityMultiplier.Returns(6UL);
        ISpecProvider? specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        IWorldState? stateProvider = Substitute.For<IWorldState>();
        stateProvider.HasStateForTargetBlock(Arg.Any<BlockHeader>()).Returns(true);

        // Stand in for the block processor: materialize the transactions and seal the header like BlockProcessor does.
        IBlockchainProcessor processor = Substitute.For<IBlockchainProcessor>();
        processor.Process(Arg.Any<Block>(), ProcessingOptions.ProducingBlock, Arg.Any<IBlockTracer>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            BlockToProduce block = ci.Arg<BlockToProduce>();
            Transaction[] transactions = block.Transactions.ToArray();
            block.Header.GasUsed = GasCostOf.Transaction * (ulong)transactions.Length;
            block.Header.Hash = block.Header.CalculateHash();
            return new Block(block.Header, transactions, block.Uncles, block.Withdrawals);
        });
        ISealEngine sealEngine = Substitute.For<ISealEngine>();
        sealEngine.CanSeal(Arg.Any<ulong>(), Arg.Any<Hash256>()).Returns(true);
        sealEngine.SealBlock(Arg.Any<Block>(), Arg.Any<CancellationToken>()).Returns(ci => Task.FromResult(ci.Arg<Block>()));
        IOptimismSpecHelper specHelper = Substitute.For<IOptimismSpecHelper>();
        specHelper.IsHolocene(Arg.Any<BlockHeader>()).Returns(true);
        // A pending transaction makes the improved block better than the empty one, so it is the block served.
        ITxSource txPoolTxSource = Substitute.For<ITxSource>();
        txPoolTxSource.GetTransactions(Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>(), Arg.Any<ulong>(), Arg.Any<PayloadAttributes>(), Arg.Any<bool>())
            .Returns([Build.A.Transaction.TestObject]);

        OptimismPostMergeBlockProducer blockProducer = new(
            payloadAttrsTxSource: Substitute.For<ITxSource>(),
            txPoolTxSource: txPoolTxSource,
            processor: processor,
            blockTree: Substitute.For<IBlockTree>(),
            stateProvider: stateProvider,
            gasLimitCalculator: Substitute.For<IGasLimitCalculator>(),
            sealEngine: sealEngine,
            timestamper: Substitute.For<ITimestamper>(),
            specProvider: specProvider,
            specHelper: specHelper,
            logManager: TestLogManager.Instance,
            blocksConfig: Substitute.For<IBlocksConfig>()
        );
        using OptimismPayloadPreparationService service = new(
            blockProducer: blockProducer,
            txPool: Substitute.For<ITxPool>(),
            blockImprovementContextFactory: new BlockImprovementContextFactory(blockProducer, TimeSpan.FromSeconds(10)),
            blocksConfig: new BlocksConfig
            {
                SecondsPerSlot = 1
            },
            timerFactory: Substitute.For<ITimerFactory>(),
            logManager: TestLogManager.Instance
        );

        testCase.Attributes.PrevRandao = Hash256.Zero;
        testCase.Attributes.SuggestedFeeRecipient = TestItem.AddressA;

        string payloadId = service.StartPreparingPayload(parent, testCase.Attributes)!;
        IBlockProductionContext context = (await service.GetPayload(payloadId, skipCancel: true))!;
        Block currentBestBlock = context.CurrentBestBlock!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(currentBestBlock.Transactions, Has.Length.EqualTo(testCase.Attributes.NoTxPool ? 0 : 1), "served block");
            Assert.That(currentBestBlock.Header.TryDecodeEIP1559Parameters(out EIP1559Parameters parameters, out _), Is.True);
            Assert.That(parameters, Is.EqualTo(testCase.ExpectedEIP1559Parameters));
            Assert.That(currentBestBlock.Header.Hash, Is.EqualTo(currentBestBlock.Header.CalculateHash()));
        }
    }

    [Test]
    public async Task Null_empty_block_is_carried_through_improvement([Values] bool noTxPool)
    {
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        Block improved = Build.A.Block.WithTransactions(Build.A.Transaction.TestObject).TestObject;

        // The empty build is rejected (e.g. over the block access list item limit) and returns null.
        IBlockProducer blockProducer = Substitute.For<IBlockProducer>();
        blockProducer.BuildBlock(Arg.Any<BlockHeader?>(), Arg.Any<IBlockTracer?>(), Arg.Any<PayloadAttributes?>(), Arg.Any<IBlockProducer.Flags>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<Block?>(ci.ArgAt<IBlockProducer.Flags>(3) == IBlockProducer.Flags.PrepareEmptyBlock ? null : improved));

        CapturingBlockImprovementContextFactory factory = new(new BlockImprovementContextFactory(blockProducer, TimeSpan.FromSeconds(10)));
        using OptimismPayloadPreparationService service = new(
            blockProducer: blockProducer,
            txPool: Substitute.For<ITxPool>(),
            blockImprovementContextFactory: factory,
            blocksConfig: new BlocksConfig { SecondsPerSlot = 1 },
            timerFactory: Substitute.For<ITimerFactory>(),
            logManager: TestLogManager.Instance);

        OptimismPayloadAttributes attributes = new() { NoTxPool = noTxPool, PrevRandao = Hash256.Zero, SuggestedFeeRecipient = TestItem.AddressA };
        string payloadId = service.StartPreparingPayload(parent, attributes);
        if (factory.Created is not null) await factory.Created.ImprovementTask;

        IBlockProductionContext? context = await service.GetPayload(payloadId, skipCancel: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context, Is.Not.Null);
            Assert.That(context?.CurrentBestBlock, noTxPool ? Is.Null : Is.SameAs(improved));
        }
    }

    private sealed class CapturingBlockImprovementContextFactory(IBlockImprovementContextFactory inner) : IBlockImprovementContextFactory
    {
        public IBlockImprovementContext? Created { get; private set; }

        public IBlockImprovementContext StartBlockImprovementContext(Block? currentBestBlock, BlockHeader parentHeader, PayloadAttributes payloadAttributes,
            DateTimeOffset startDateTime, UInt256 currentBlockFees, SharedCancellationTokenSource cts) =>
            Created = inner.StartBlockImprovementContext(currentBestBlock, parentHeader, payloadAttributes, startDateTime, currentBlockFees, cts);
    }
}
