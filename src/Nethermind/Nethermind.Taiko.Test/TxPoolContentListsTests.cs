// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Taiko.Rpc;
using Nethermind.TxPool;
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
using Nethermind.Api;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Consensus.Processing;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Taiko.Test;

public class TxPoolContentListsTests
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
        transactionProcessor.When(static (x) => x.Execute(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<ITxTracer>()))
            .Do(static info => ((BlockExecutionContext)info[1]).Header.GasUsed += Transaction.BaseTxGasCost);

        transactionProcessor.Execute(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<ITxTracer>())
            .Returns(static info =>
            {
                if (((BlockExecutionContext)info[1]).Header.GasUsed <= ((BlockExecutionContext)info[1]).Header.GasLimit)
                    return TransactionResult.Ok;

                ((BlockExecutionContext)info[1]).Header.GasUsed -= Transaction.BaseTxGasCost;
                return TransactionResult.BlockGasLimitExceeded;
            });

        IReadOnlyTxProcessingScope scope = Substitute.For<IReadOnlyTxProcessingScope>();
        scope.TransactionProcessor.Returns(transactionProcessor);

        IReadOnlyTxProcessorSource txProcessorSource = Substitute.For<IReadOnlyTxProcessorSource>();
        txProcessorSource.Build(Arg.Any<Hash256>()).Returns(scope);

        IReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory = Substitute.For<IReadOnlyTxProcessingEnvFactory>();
        readOnlyTxProcessingEnvFactory.Create().Returns(txProcessorSource);

        TaikoEngineRpcModule taikoRpcModule = new(
            Substitute.For<IAsyncHandler<byte[], ExecutionPayload?>>(),
            Substitute.For<IAsyncHandler<byte[], GetPayloadV2Result?>>(),
            Substitute.For<IAsyncHandler<byte[], GetPayloadV3Result?>>(),
            Substitute.For<IAsyncHandler<byte[], GetPayloadV4Result?>>(),
            Substitute.For<IAsyncHandler<byte[], GetPayloadV5Result?>>(),
            Substitute.For<IAsyncHandler<ExecutionPayload, PayloadStatusV1>>(),
            Substitute.For<IForkchoiceUpdatedHandler>(),
            Substitute.For<IHandler<IReadOnlyList<Hash256>, IEnumerable<ExecutionPayloadBodyV1Result?>>>(),
            Substitute.For<IGetPayloadBodiesByRangeV1Handler>(),
            Substitute.For<IHandler<TransitionConfigurationV1, TransitionConfigurationV1>>(),
            Substitute.For<IHandler<IEnumerable<string>, IEnumerable<string>>>(),
            Substitute.For<IAsyncHandler<byte[][], IEnumerable<BlobAndProofV1?>>>(),
            Substitute.For<IAsyncHandler<byte[][], IEnumerable<BlobAndProofV2>?>>(),
            Substitute.For<IEngineRequestsTracker>(),
            Substitute.For<ISpecProvider>(),
            null!,
            Substitute.For<ILogManager>(),
            txPool,
            blockFinder,
            readOnlyTxProcessingEnvFactory,
            TxDecoder.Instance
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

        return result.Data!.Select(static list => list.TxList.OfType<EIP1559TransactionForRpc>().Select(static tx => (int)tx.Input![0]).ToArray()).ToArray();
    }

    public static IEnumerable FinalizingTests
    {
        get
        {
            static object[] MakeTestData(Dictionary<int, int[]> txs, int[] localAccounts, ulong blockGasLimit, ulong maxBytesPerTxList, int maxTransactionsLists)
            {
                return [
                    txs.ToDictionary(
                        static kv => (AddressAsKey)Build.An.Address.FromNumber(kv.Key).TestObject,
                        static kv => kv.Value.Select(static txId =>
                            Build.A.Transaction.WithType(TxType.EIP1559).WithMaxFeePerGas(7).WithNonce(1).WithValue(1).WithGasPrice(20).WithData([(byte)txId]).SignedAndResolved().TestObject
                        ).ToArray()),
                    localAccounts.Select(static a => Build.An.Address.FromNumber(a).TestObject).ToArray(),
                    blockGasLimit,
                    maxBytesPerTxList,
                    maxTransactionsLists
                ];
            }

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

            yield return new TestCaseData(args: MakeTestData(new Dictionary<int, int[]> { { 1, [1] }, { 2, [2] }, { 3, [3] } }, [], 10 * Transaction.BaseTxGasCost, 120, 1))
            {
                TestName = "Considers compressed tx list size limit",
                ExpectedResult = new int[][] { [1] },
            };
        }
    }
}
