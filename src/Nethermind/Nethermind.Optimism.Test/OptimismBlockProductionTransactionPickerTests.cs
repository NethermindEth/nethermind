// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Optimism.Rpc;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

public class OptimismBlockProductionTransactionPickerTests
{
    [Test]
    public async Task Payload_build_requires_valid_supplied_transactions([Values] bool noTxPool, [Values] bool funded)
    {
        Transaction required = Build.A.Transaction.WithGasLimit(21_000).WithValue(1).SignedAndResolved().TestObject;
        Transaction optional = Build.A.Transaction.WithSenderAddress(TestItem.AddressB).WithGasLimit(21_000).TestObject;
        OptimismPayloadAttributes attributes = new()
        {
            GasLimit = 1_000_000,
            NoTxPool = noTxPool,
            PrevRandao = Hash256.Zero,
            SuggestedFeeRecipient = TestItem.AddressA,
            Transactions = [Rlp.Encode(required, RlpBehaviors.SkipTypedWrapping)],
        };
        IWorldState state = Substitute.For<IWorldState>();
        state.HasStateForTargetBlock(Arg.Any<BlockHeader>()).Returns(true);
        IReadOnlyStateProvider balances = state;
        balances.GetBalance(required.SenderAddress!).Returns(funded ? UInt256.MaxValue : UInt256.Zero);
        TestSpecProvider specProvider = new(Byzantium.Instance);
        OptimismBlockProductionTransactionPicker picker = new(specProvider, maxTxLengthKilobytes: 0);
        ITxSource txPool = Substitute.For<ITxSource>();
        txPool.GetTransactions(Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>(), Arg.Any<ulong>(), attributes, true)
            .Returns([optional]);

        ITransactionProcessorAdapter adapter = Substitute.For<ITransactionProcessorAdapter>();
        adapter.Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.Ok);
        BlockProcessor.BlockProductionTransactionsExecutor executor = new(
            adapter, state, picker, TestLogManager.Instance, NullBlockAccessListManager.Instance, NullTxPool.Instance);
        IBlockchainProcessor processor = Substitute.For<IBlockchainProcessor>();
        processor.Process(Arg.Any<Block>(), ProcessingOptions.ProducingBlock, Arg.Any<IBlockTracer>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                BlockToProduce prepared = call.Arg<BlockToProduce>();
                attributes.GetTransactions()![0].SenderAddress = required.SenderAddress;
                BlockReceiptsTracer receiptsTracer = new();
                receiptsTracer.StartNewBlockTrace(prepared);
                try
                {
                    executor.ProcessTransactions(prepared, ProcessingOptions.ProducingBlock, receiptsTracer);
                    return prepared;
                }
                catch (InvalidBlockException)
                {
                    return null;
                }
            });

        OptimismPostMergeBlockProducer producer = new(
            new OptimismPayloadTxSource(), txPool, processor, Substitute.For<IBlockTree>(), state,
            Substitute.For<IGasLimitCalculator>(), Substitute.For<ISealEngine>(), Substitute.For<ITimestamper>(),
            specProvider, Substitute.For<IOptimismSpecHelper>(), TestLogManager.Instance, new BlocksConfig());
        IBlockProducer.Flags flags = IBlockProducer.Flags.DontSeal;
        if (noTxPool) flags |= IBlockProducer.Flags.EmptyBlock;
        Block? block = await producer.BuildBlock(Build.A.BlockHeader.TestObject, payloadAttributes: attributes, flags: flags);
        using (Assert.EnterMultipleScope())
        {
            if (funded)
                Assert.That(block?.Transactions.Select(static tx => tx.Hash), Is.EqualTo(new[] { required.Hash }));
            else
                Assert.That(block, Is.Null);
            txPool.Received(noTxPool ? 0 : 1)
                .GetTransactions(Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>(), Arg.Any<ulong>(), attributes, true);
        }
    }
}
