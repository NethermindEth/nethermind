// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Specs;
using Nethermind.Facade.Eth;
using Nethermind.Facade;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Taiko.Rpc;
using Nethermind.TxPool;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Core;
using System.Collections.Generic;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm;
using System.Collections;
using System.Linq;

namespace Nethermind.Taiko.Test;

public class TxPoolContentLists
{
    [TestCaseSource(nameof(FinalizingTests))]
    public int[][] Test_TxLists_AreConstructed(
        Dictionary<AddressAsKey, Transaction[]> transactions,
        Address[]? localAccounts,
        ulong blockGasLimit,
        ulong maxBytesPerTxList,
        int maxTransactionsLists)
    {
        ITxPool txPool = Substitute.For<ITxPool>();
        txPool.GetPendingTransactionsBySender().ReturnsForAnyArgs(transactions);

        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        Block block = Build.A.Block.WithHeader(Build.A.BlockHeader.WithGasLimit((long)blockGasLimit).TestObject).TestObject;
        blockFinder.Head.Returns(block);

        ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
        transactionProcessor.When((x) => x.Execute(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<ITxTracer>()))
            .Do(info =>
            {
                if (((BlockExecutionContext)info[1]).Header.GasUsed + Transaction.BaseTxGasCost <= ((BlockExecutionContext)info[1]).Header.GasLimit)
                {
                    ((BlockExecutionContext)info[1]).Header.GasUsed += Transaction.BaseTxGasCost;
                }
            });

        transactionProcessor.Execute(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<ITxTracer>())
            .Returns(info => ((BlockExecutionContext)info[1]).Header.GasUsed - Transaction.BaseTxGasCost < ((BlockExecutionContext)info[1]).Header.GasLimit ? TransactionResult.Ok : TransactionResult.BlockGasLimitExceeded);

        IReadOnlyTxProcessingScope scope = Substitute.For<IReadOnlyTxProcessingScope>();
        scope.TransactionProcessor.Returns(transactionProcessor);

        IReadOnlyTxProcessorSource txProcessorSource = Substitute.For<IReadOnlyTxProcessorSource>();
        txProcessorSource.Build(Arg.Any<Hash256>()).Returns(scope);

        var taikoRpcModule = new TaikoRpcModule(
             Substitute.For<IJsonRpcConfig>(),
             Substitute.For<IBlockchainBridge>(),
             blockFinder,
             Substitute.For<IReceiptFinder>(),
             Substitute.For<IStateReader>(),
             txPool,
             Substitute.For<ITxSender>(),
             Substitute.For<IWallet>(),
             Substitute.For<ILogManager>(),
             Substitute.For<ISpecProvider>(),
             Substitute.For<IGasPriceOracle>(),
             Substitute.For<IEthSyncingInfo>(),
             Substitute.For<IFeeHistoryOracle>(),
             12,
             Substitute.For<ISyncConfig>(),
             Substitute.For<IL1OriginStore>(),
             txProcessorSource
         );

        ResultWrapper<PreBuiltTxList[]?> result = taikoRpcModule.taikoAuth_txPoolContent(
            Address.Zero,
            7,
            blockGasLimit,
            maxBytesPerTxList,
            localAccounts,
            maxTransactionsLists);

        Assert.That(result.Result, Is.EqualTo(Result.Success));
        Assert.That(result.Data, Is.Not.Null);

        return result.Data.Select(list => list.TxList.Select(tx => (int)tx.Input![0]).ToArray()).ToArray();
    }

    public static IEnumerable FinalizingTests
    {
        get
        {
            static object[] MakeTestData(Dictionary<int, int[]> txs, int[] localAccounts, ulong blockGasLimit, ulong maxBytesPerTxList, int maxTransactionsLists)
            {
                return [
                    txs.ToDictionary(
                        kv => (AddressAsKey)Build.An.Address.FromNumber(kv.Key).TestObject,
                        kv => kv.Value.Select(v =>
                            Build.A.Transaction.WithType(TxType.Legacy).WithNonce(1).WithValue(1).WithGasPrice(20).WithData([(byte)v]).SignedAndResolved().TestObject
                        ).ToArray()),
                    localAccounts.Select(a => Build.An.Address.FromNumber(a).TestObject).ToArray(),
                    blockGasLimit,
                    maxBytesPerTxList,
                    maxTransactionsLists
                ];
            };

            yield return new TestCaseData(args: MakeTestData(new Dictionary<int, int[]> { { 1, [1] }, { 2, [2] }, { 3, [3] } }, [], 2 * Transaction.BaseTxGasCost, 1000, 2))
            {
                TestName = "Splits in lists",
                ExpectedResult = new int[][] { [1, 2], [3] },
            };
            yield return new TestCaseData(args: MakeTestData(new Dictionary<int, int[]> { { 1, [1] }, { 2, [2] }, { 3, [3] } }, [], 2 * Transaction.BaseTxGasCost, 1000, 1))
            {
                TestName = "Does not generate more lists than requested",
                ExpectedResult = new int[][] { [1, 2] },
            };
            yield return new TestCaseData(args: MakeTestData(new Dictionary<int, int[]> { { 1, [1] }, { 2, [2] }, { 3, [3] } }, [2], 10 * Transaction.BaseTxGasCost, 1000, 1))
            {
                TestName = "Local accounts are in priority",
                ExpectedResult = new int[][] { [2, 1, 3] },
            };
            yield return new TestCaseData(args: MakeTestData(new Dictionary<int, int[]> { { 1, [1] }, { 2, [20, 21] }, { 3, [3] }, { 4, [40, 41] } }, [2, 4], 10 * Transaction.BaseTxGasCost, 1000, 1))
            {
                TestName = "Several local accounts are in priority",
                ExpectedResult = new int[][] { [20, 21, 40, 41, 1, 3] },
            };

            yield return new TestCaseData(args: MakeTestData(new Dictionary<int, int[]> { { 1, [1] }, { 2, [2] }, { 3, [3] } }, [], 10 * Transaction.BaseTxGasCost, 100, 1))
            {
                TestName = "Considers compressed tx list size limit",
                ExpectedResult = new int[][] { [1] },
            };
        }
    }
}
