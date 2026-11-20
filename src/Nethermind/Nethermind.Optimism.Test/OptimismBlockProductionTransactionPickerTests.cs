// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Optimism.Rpc;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

public class OptimismBlockProductionTransactionPickerTests
{
    [Test]
    public async Task Local_byte_limit_keeps_payload_transactions([Values] bool noTxPool)
    {
        Transaction required = Build.A.Transaction.WithGasLimit(21_000).SignedAndResolved().TestObject;
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
        IReadOnlyStateProvider balances = Substitute.For<IReadOnlyStateProvider>();
        balances.GetBalance(required.SenderAddress!).Returns(UInt256.MaxValue);
        TestSpecProvider specProvider = new(Byzantium.Instance);
        OptimismBlockProductionTransactionPicker picker = new(specProvider, maxTxLengthKilobytes: 0);
        ITxSource txPool = Substitute.For<ITxSource>();
        txPool.GetTransactions(Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>(), Arg.Any<ulong>(), attributes, true)
            .Returns([optional]);

        IBlockchainProcessor processor = Substitute.For<IBlockchainProcessor>();
        processor.Process(Arg.Any<Block>(), ProcessingOptions.ProducingBlock, Arg.Any<IBlockTracer>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                BlockToProduce prepared = call.Arg<BlockToProduce>();
                HashSet<Transaction> included = [];
                foreach (Transaction tx in prepared.Transactions)
                {
                    tx.SenderAddress ??= required.SenderAddress;
                    if (picker.CanAddTransaction(prepared, tx, included, balances, 0, 0).Action == BlockProcessor.TxAction.Add)
                        included.Add(tx);
                }
                return new Block(prepared.Header, included.ToArray(), prepared.Uncles, prepared.Withdrawals);
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
            Assert.That(block?.Transactions.Select(static tx => tx.Hash), Is.EqualTo(new[] { required.Hash }));
            txPool.Received(noTxPool ? 0 : 1)
                .GetTransactions(Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>(), Arg.Any<ulong>(), attributes, true);
        }
    }
}
