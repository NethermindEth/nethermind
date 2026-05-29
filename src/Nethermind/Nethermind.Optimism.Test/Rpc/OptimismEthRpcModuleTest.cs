// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.History;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db.LogIndex;
using Nethermind.Evm;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.Optimism.Rpc;
using Nethermind.Serialization.Rlp;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.TxPool;
using NSubstitute;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.Rpc;

public class OptimismEthRpcModuleTest
{
    [SetUp]
    public void Setup()
    {
        TransactionForRpc.RegisterTransactionType<DepositTransactionForRpc>();
        TxDecoder.Instance.RegisterDecoder(new OptimismTxDecoder<Transaction>());
        TxDecoder.Instance.RegisterDecoder(new OptimismLegacyTxDecoder());
    }

    private static IBlockFinder MockBlockFinder(Block block)
    {
        IBlockFinder bf = Substitute.For<IBlockFinder>();
        bf.FindBlock(new BlockParameter(block.Hash!)).Returns(block);
        bf.FindBlock(new BlockParameter(block.Number)).Returns(block);
        return bf;
    }

    private static IReceiptFinder MockReceiptFinder(Block block, params TxReceipt[] receipts)
    {
        IReceiptFinder rf = Substitute.For<IReceiptFinder>();
        rf.Get(block).Returns(receipts);
        return rf;
    }

    private static Task<TestRpcBlockchain> BuildOptimismRpc(IBlockFinder blockFinder, IReceiptFinder receiptFinder) =>
        TestRpcBlockchain
            .ForTest(sealEngineType: SealEngineType.Optimism)
            .WithBlockFinder(blockFinder)
            .WithReceiptFinder(receiptFinder)
            .WithOptimismEthRpcModule(
                sequencerRpcClient: Substitute.For<IJsonRpcClient>(),
                accountStateProvider: Substitute.For<IAccountStateProvider>(),
                ecdsa: Substitute.For<IEthereumEcdsa>(),
                sealer: Substitute.For<ITxSealer>(),
                opSpecHelper: Substitute.For<IOptimismSpecHelper>())
            .Build();

    [Test]
    public async Task Sequencer_send_transaction_with_signature_will_not_try_to_sign()
    {
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        ITxSender txSender = Substitute.For<ITxSender>();
        txSender.SendTransaction(tx: Arg.Any<Transaction>(), txHandlingOptions: TxHandlingOptions.PersistentBroadcast)
            .Returns(returnThis: (TestItem.KeccakA, AcceptTxResult.Accepted));

        EthereumEcdsa ethereumEcdsa = new(chainId: TestBlockchainIds.ChainId);
        TestRpcBlockchain rpcBlockchain = await TestRpcBlockchain
            .ForTest(sealEngineType: SealEngineType.Optimism)
            .WithBlockchainBridge(bridge)
            .WithTxSender(txSender)
            .WithOptimismEthRpcModule(
                sequencerRpcClient: null /* explicitly using null to behave as Sequencer */,
                accountStateProvider: Substitute.For<IAccountStateProvider>(),
                ecdsa: new OptimismEthereumEcdsa(ethereumEcdsa),
                sealer: Substitute.For<ITxSealer>(),
                opSpecHelper: Substitute.For<IOptimismSpecHelper>())
            .Build();

        Transaction tx = Build.A.Transaction
            .Signed(ecdsa: ethereumEcdsa, privateKey: TestItem.PrivateKeyA)
            .TestObject;
        string serialized = await rpcBlockchain.TestEthRpc("eth_sendRawTransaction", Rlp.Encode(item: tx, behaviors: RlpBehaviors.None).Bytes.ToHexString());

        await txSender.Received().SendTransaction(tx: Arg.Any<Transaction>(), txHandlingOptions: TxHandlingOptions.PersistentBroadcast);
        Assert.That(serialized, Is.EqualTo($$"""{"jsonrpc":"2.0","result":"{{TestItem.KeccakA.Bytes.ToHexString(withZeroX: true)}}","id":67}"""));
    }

    [Test]
    public async Task GetTransactionByHash_ReturnsCorrectTransactionType()
    {
        Transaction tx = Build.A.Transaction
            .WithType(TxType.Legacy)
            .WithHash(TestItem.KeccakA)
            .WithSenderAddress(TestItem.AddressA)
            .TestObject;
        TxReceipt receipt = new()
        {
            BlockHash = TestItem.KeccakB,
            BlockNumber = 0x10,
            Index = 0x20
        };

        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        TransactionLookupResult lookupResult = new(
            tx,
            new(
                chainId: 0,
                blockHash: receipt.BlockHash!,
                blockNumber: receipt.BlockNumber,
                txIndex: receipt.Index,
                blockTimestamp: 0,
                baseFee: 0,
                receipt: receipt));
        bridge.TryGetTransaction(TestItem.KeccakA, out Arg.Any<TransactionLookupResult?>(), checkTxnPool: true)
            .Returns(callInfo =>
            {
                callInfo[1] = lookupResult;
                return true;
            });

        TestRpcBlockchain rpcBlockchain = await TestRpcBlockchain
            .ForTest(sealEngineType: SealEngineType.Optimism)
            .WithBlockchainBridge(bridge)
            .WithOptimismEthRpcModule(
                sequencerRpcClient: Substitute.For<IJsonRpcClient>(),
                accountStateProvider: Substitute.For<IAccountStateProvider>(),
                ecdsa: Substitute.For<IEthereumEcdsa>(),
                sealer: Substitute.For<ITxSealer>(),
                opSpecHelper: Substitute.For<IOptimismSpecHelper>())
            .Build();


        string serialized = await rpcBlockchain.TestEthRpc("eth_getTransactionByHash", TestItem.KeccakA);
        string expected = $$"""
                         {
                            "jsonrpc":"2.0",
                            "result": {
                                 "type": "0x0",
                                 "from": "{{TestItem.AddressA.Bytes.ToHexString(withZeroX: true)}}",
                                 "to": "0x0000000000000000000000000000000000000000",
                                 "value": "0x1",
                                 "gas": "0x5208",
                                 "gasPrice": "0x1",
                                 "input": "0x",
                                 "nonce": "0x0",
                                 "v": "0x0",
                                 "r": "0x0",
                                 "s": "0x0",
                                 "hash": "{{TestItem.KeccakA.Bytes.ToHexString(withZeroX: true)}}",
                                 "blockHash": "{{TestItem.KeccakB.Bytes.ToHexString(withZeroX: true)}}",
                                 "blockNumber": "0x10",
                                 "blockTimestamp": "0x0",
                                 "transactionIndex": "0x20"
                             },
                            "id":67
                         }
                         """;
        Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse(expected)).Using(JToken.EqualityComparer));
    }

    [Test]
    public async Task GetTransactionByHash_IncludesDepositReceiptVersion()
    {
        Transaction tx = Build.A.Transaction
            .WithType(TxType.DepositTx)
            .WithHash(TestItem.KeccakA)
            .WithSenderAddress(TestItem.AddressA)
            .TestObject;
        OptimismTxReceipt receipt = new()
        {
            BlockHash = TestItem.KeccakB,
            BlockNumber = 0x10,
            Index = 0x20,
            DepositReceiptVersion = 0x30,
        };

        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        TransactionLookupResult lookupResult = new(
            tx,
            new(
                chainId: 0,
                blockHash: receipt.BlockHash!,
                blockNumber: receipt.BlockNumber,
                txIndex: receipt.Index,
                blockTimestamp: 0,
                baseFee: 0,
                receipt: receipt));
        bridge.TryGetTransaction(TestItem.KeccakA, out Arg.Any<TransactionLookupResult?>(), checkTxnPool: true)
            .Returns(callInfo =>
            {
                callInfo[1] = lookupResult;
                return true;
            });

        TestRpcBlockchain rpcBlockchain = await TestRpcBlockchain
            .ForTest(sealEngineType: SealEngineType.Optimism)
            .WithBlockchainBridge(bridge)
            .WithOptimismEthRpcModule(
                sequencerRpcClient: Substitute.For<IJsonRpcClient>(),
                accountStateProvider: Substitute.For<IAccountStateProvider>(),
                ecdsa: Substitute.For<IEthereumEcdsa>(),
                sealer: Substitute.For<ITxSealer>(),
                opSpecHelper: Substitute.For<IOptimismSpecHelper>())
            .Build();


        string serialized = await rpcBlockchain.TestEthRpc("eth_getTransactionByHash", TestItem.KeccakA);
        string expected = $$"""
                         {
                            "jsonrpc":"2.0",
                            "result": {
                                 "type": "0x7e",
                                 "sourceHash":"0x0000000000000000000000000000000000000000000000000000000000000000",
                                 "from": "{{TestItem.AddressA.Bytes.ToHexString(withZeroX: true)}}",
                                 "to": "0x0000000000000000000000000000000000000000",
                                 "mint": "0x0",
                                 "value": "0x1",
                                 "gas": "0x5208",
                                 "isSystemTx": false,
                                 "input": "0x",
                                 "nonce": "0x0",
                                 "depositReceiptVersion": "0x30",
                                 "hash": "{{TestItem.KeccakA.Bytes.ToHexString(withZeroX: true)}}",
                                 "blockHash": "{{TestItem.KeccakB.Bytes.ToHexString(withZeroX: true)}}",
                                 "blockNumber": "0x10",
                                 "blockTimestamp": "0x0",
                                 "transactionIndex": "0x20"
                             },
                            "id":67
                         }
                         """;
        Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse(expected)).Using(JToken.EqualityComparer));
    }

    [Test]
    public async Task GetTransactionByBlockAndIndex_ReturnsCorrectTransactionType()
    {
        Transaction tx = Build.A.Transaction
            .WithType(TxType.Legacy)
            .WithHash(TestItem.KeccakA)
            .WithSenderAddress(TestItem.AddressA)
            .TestObject;
        Block block = Build.A.Block
            .WithHeader(Build.A.BlockHeader
                .WithNumber(0x10)
                .TestObject)
            .WithTransactions(tx)
            .TestObject;
        OptimismTxReceipt receipt = new()
        {
            TxHash = tx.Hash!,
            BlockHash = block.Hash!,
            BlockNumber = block.Number,
            Index = 0x20,
            DepositReceiptVersion = 0x30,
        };

        TestRpcBlockchain rpcBlockchain = await BuildOptimismRpc(MockBlockFinder(block), MockReceiptFinder(block, receipt));

        string expected = $$"""
                         {
                            "jsonrpc":"2.0",
                            "result": {
                                "type": "0x0",
                                "from": "{{tx.SenderAddress!.Bytes.ToHexString(withZeroX: true)}}",
                                "to": "0x0000000000000000000000000000000000000000",
                                "value": "0x1",
                                "gas": "0x5208",
                                "gasPrice": "0x1",
                                "input": "0x",
                                "nonce": "0x0",
                                "v": "0x0",
                                "r": "0x0",
                                "s": "0x0",
                                "hash": "{{tx.Hash!.Bytes.ToHexString(withZeroX: true)}}",
                                "blockHash": "{{block.Hash!.Bytes.ToHexString(withZeroX: true)}}",
                                "blockNumber": "0x10",
                                "blockTimestamp": "0xf4240",
                                "transactionIndex": "0x0"
                            },
                            "id":67
                         }
                         """;
        {
            // By block hash
            string serialized = await rpcBlockchain.TestEthRpc("eth_getTransactionByBlockHashAndIndex", block.Hash, 0);
            Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse(expected)).Using(JToken.EqualityComparer));
        }
        {
            // By block number
            string serialized = await rpcBlockchain.TestEthRpc("eth_getTransactionByBlockNumberAndIndex", block.Number, 0);
            Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse(expected)).Using(JToken.EqualityComparer));
        }
    }

    [Test]
    public async Task GetTransactionByBlockAndIndex_IncludesDepositReceiptVersion()
    {
        Transaction tx = Build.A.Transaction
            .WithType(TxType.DepositTx)
            .WithHash(TestItem.KeccakA)
            .WithSenderAddress(TestItem.AddressA)
            .TestObject;
        Block block = Build.A.Block
            .WithHeader(Build.A.BlockHeader
                .WithNumber(0x10)
                .TestObject)
            .WithTransactions(tx)
            .TestObject;
        OptimismTxReceipt receipt = new()
        {
            TxHash = tx.Hash!,
            BlockHash = block.Hash!,
            BlockNumber = block.Number,
            Index = 0x20,
            DepositReceiptVersion = 0x30,
        };

        TestRpcBlockchain rpcBlockchain = await BuildOptimismRpc(MockBlockFinder(block), MockReceiptFinder(block, receipt));

        string expected = $$"""
                         {
                            "jsonrpc":"2.0",
                            "result": {
                                 "type": "0x7e",
                                 "sourceHash":"0x0000000000000000000000000000000000000000000000000000000000000000",
                                 "from": "{{tx.SenderAddress!.Bytes.ToHexString(withZeroX: true)}}",
                                 "to": "0x0000000000000000000000000000000000000000",
                                 "mint": "0x0",
                                 "value": "0x1",
                                 "gas": "0x5208",
                                 "isSystemTx": false,
                                 "input": "0x",
                                 "nonce": "0x0",
                                 "depositReceiptVersion": "0x30",
                                 "hash": "{{tx.Hash!.Bytes.ToHexString(withZeroX: true)}}",
                                 "blockHash": "{{block.Hash!.Bytes.ToHexString(withZeroX: true)}}",
                                 "blockNumber": "0x10",
                                 "blockTimestamp": "0xf4240",
                                 "transactionIndex": "0x0"
                             },
                            "id":67
                         }
                         """;
        {
            // By block hash
            string serialized = await rpcBlockchain.TestEthRpc("eth_getTransactionByBlockHashAndIndex", block.Hash, 0);
            Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse(expected)).Using(JToken.EqualityComparer));
        }
        {
            // By block number
            string serialized = await rpcBlockchain.TestEthRpc("eth_getTransactionByBlockNumberAndIndex", block.Number, 0);
            Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse(expected)).Using(JToken.EqualityComparer));
        }
    }

    public enum ReceiptOrder { Aligned, Reversed, Empty }

    [TestCase(ReceiptOrder.Aligned, "0x1", TestName = "Receipts aligned with transactions (fast path)")]
    [TestCase(ReceiptOrder.Reversed, "0x1", TestName = "Receipts in reverse order (linear fallback)")]
    [TestCase(ReceiptOrder.Empty, null, TestName = "Empty receipts (no match)")]
    public async Task GetBlockByHash_WithFullTransactions_SetsDepositReceiptVersionFromMatchingReceipt(
        ReceiptOrder order, string? expectedDepositVersion)
    {
        Transaction depositTx = Build.A.Transaction
            .WithType(TxType.DepositTx)
            .WithHash(TestItem.KeccakA)
            .WithSenderAddress(TestItem.AddressA)
            .TestObject;
        Transaction regularTx = Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithHash(TestItem.KeccakB)
            .WithSenderAddress(TestItem.AddressB)
            .TestObject;

        Block block = Build.A.Block
            .WithHeader(Build.A.BlockHeader
                .WithNumber(1)
                .WithHash(TestItem.KeccakC)
                .TestObject)
            .WithTransactions(depositTx, regularTx)
            .TestObject;

        OptimismTxReceipt depositReceipt = new()
        {
            Sender = depositTx.SenderAddress!,
            TxType = depositTx.Type,
            TxHash = depositTx.Hash!,
            BlockHash = block.Hash,
            BlockNumber = block.Number,
            Index = 0,
            DepositReceiptVersion = 1,
        };
        TxReceipt regularReceipt = new()
        {
            Sender = regularTx.SenderAddress!,
            TxType = regularTx.Type,
            TxHash = regularTx.Hash!,
            BlockHash = block.Hash,
            BlockNumber = block.Number,
            Index = 1,
        };

        TxReceipt[] receipts = order switch
        {
            ReceiptOrder.Aligned => [depositReceipt, regularReceipt],
            ReceiptOrder.Reversed => [regularReceipt, depositReceipt],
            ReceiptOrder.Empty => [],
            _ => throw new System.ArgumentOutOfRangeException(nameof(order)),
        };

        TestRpcBlockchain rpcBlockchain = await BuildOptimismRpc(MockBlockFinder(block), MockReceiptFinder(block, receipts));

        string serialized = await rpcBlockchain.TestEthRpc("eth_getBlockByHash", block.Hash, true);
        JToken result = JToken.Parse(serialized)["result"]!;
        JToken firstTx = result["transactions"]![0]!;
        JToken secondTx = result["transactions"]![1]!;

        Assert.That(firstTx["hash"]!.Value<string>(), Is.EqualTo(depositTx.Hash!.Bytes.ToHexString(withZeroX: true)));
        if (expectedDepositVersion is null)
            Assert.That(firstTx["depositReceiptVersion"], Is.Null);
        else
            Assert.That(firstTx["depositReceiptVersion"]!.Value<string>(), Is.EqualTo(expectedDepositVersion));
        Assert.That(secondTx["hash"]!.Value<string>(), Is.EqualTo(regularTx.Hash!.Bytes.ToHexString(withZeroX: true)));
        Assert.That(secondTx["depositReceiptVersion"], Is.Null);
    }

    [Test]
    public async Task GetBlockReceipts_ReturnsDefaultAndOptimismReceipts()
    {
        Transaction txA = Build.A.Transaction
            .WithType(TxType.DepositTx)
            .WithHash(TestItem.KeccakA)
            .WithSenderAddress(TestItem.AddressA)
            .TestObject;

        Transaction txB = Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithHash(TestItem.KeccakB)
            .WithSenderAddress(TestItem.AddressA)
            .TestObject;

        Block block = Build.A.Block
            .WithHeader(Build.A.BlockHeader
                .WithNumber(1)
                .WithHash(TestItem.KeccakC)
                .TestObject)
            .WithTransactions(txA, txB)
            .TestObject;

        OptimismTxReceipt receiptA = new()
        {
            Sender = txA.SenderAddress!,
            TxType = txA.Type,
            TxHash = txA.Hash!,
            BlockHash = block.Hash,
            BlockNumber = 1,
            Index = 0,
            DepositReceiptVersion = 1,
            DepositNonce = 2,
        };

        TxReceipt receiptB = new()
        {
            Sender = txB.SenderAddress!,
            TxType = txB.Type,
            TxHash = txB.Hash!,
            BlockHash = block.Hash,
            BlockNumber = 1,
            Index = 1,
        };

        TestRpcBlockchain rpcBlockchain = await BuildOptimismRpc(MockBlockFinder(block), MockReceiptFinder(block, receiptA, receiptB));

        string serialized = await rpcBlockchain.TestEthRpc("eth_getBlockReceipts", new BlockParameter(block.Number));
        string expected = $$"""
                         {
                            "jsonrpc":"2.0",
                            "result": [
                                {
                                    "transactionHash": "{{txA.Hash!.Bytes.ToHexString(withZeroX: true)}}",
                                    "transactionIndex": "0x0",
                                    "blockHash": "{{block.Hash!.Bytes.ToHexString(withZeroX: true)}}",
                                    "blockNumber": "0x1",
                                    "cumulativeGasUsed": "0x0",
                                    "gasUsed": "0x0",
                                    "effectiveGasPrice": "0x1",
                                    "from": "{{txA.SenderAddress!.Bytes.ToHexString(withZeroX: true)}}",
                                    "to": null,
                                    "contractAddress": null,
                                    "logs": [],
                                    "logsBloom": "0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
                                    "status": "0x0",
                                    "type": "0x7e",
                                    "depositReceiptVersion": "0x1",
                                    "depositNonce": "0x2",
                                },
                                {
                                    "transactionHash": "{{txB.Hash!.Bytes.ToHexString(withZeroX: true)}}",
                                    "transactionIndex": "0x1",
                                    "blockHash": "{{block.Hash!.Bytes.ToHexString(withZeroX: true)}}",
                                    "blockNumber": "0x1",
                                    "cumulativeGasUsed": "0x0",
                                    "gasUsed": "0x0",
                                    "effectiveGasPrice": "0x1",
                                    "from": "{{txA.SenderAddress!.Bytes.ToHexString(withZeroX: true)}}",
                                    "to": null,
                                    "contractAddress": null,
                                    "logs": [],
                                    "logsBloom": "0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
                                    "status": "0x0",
                                    "type": "0x2"
                                }
                            ],
                            "id":67
                         }
                         """;
        Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse(expected)).Using(JToken.EqualityComparer));
    }

    [Test]
    public async Task GetTransactionReceipt_ReturnsDepositReceipt()
    {
        Transaction tx = Build.A.Transaction
            .WithType(TxType.DepositTx)
            .WithHash(TestItem.KeccakA)
            .WithSenderAddress(TestItem.AddressA)
            .TestObject;

        ulong timestamp = 10;
        Block block = Build.A.Block
            .WithHeader(Build.A.BlockHeader
                .WithNumber(1)
                .WithHash(TestItem.KeccakC)
                .TestObject)
            .WithTimestamp(timestamp)
            .WithTransactions(tx)
            .TestObject;

        OptimismTxReceipt receipt = new()
        {
            Sender = tx.SenderAddress!,
            TxType = tx.Type,
            TxHash = tx.Hash!,
            BlockHash = block.Hash,
            BlockNumber = 1,
            Index = 0,
            DepositReceiptVersion = 1,
        };

        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        bridge.GetTxReceiptInfo(tx.Hash!).Returns((receipt, timestamp, new TxGasInfo(1), 0));

        TestRpcBlockchain rpcBlockchain = await TestRpcBlockchain
            .ForTest(sealEngineType: SealEngineType.Optimism)
            .WithBlockchainBridge(bridge)
            .WithBlockFinder(MockBlockFinder(block))
            .WithOptimismEthRpcModule(
                sequencerRpcClient: Substitute.For<IJsonRpcClient>(),
                accountStateProvider: Substitute.For<IAccountStateProvider>(),
                ecdsa: Substitute.For<IEthereumEcdsa>(),
                sealer: Substitute.For<ITxSealer>(),
                opSpecHelper: Substitute.For<IOptimismSpecHelper>())
            .Build();


        string serialized = await rpcBlockchain.TestEthRpc("eth_getTransactionReceipt", tx.Hash);
        string expected = $$"""
                         {
                            "jsonrpc":"2.0",
                            "result": {
                                "transactionHash": "{{tx.Hash!.Bytes.ToHexString(withZeroX: true)}}",
                                "transactionIndex": "0x0",
                                "blockHash": "{{block.Hash!.Bytes.ToHexString(withZeroX: true)}}",
                                "blockNumber": "0x1",
                                "cumulativeGasUsed": "0x0",
                                "gasUsed": "0x0",
                                "effectiveGasPrice": "0x1",
                                "from": "{{tx.SenderAddress!.Bytes.ToHexString(withZeroX: true)}}",
                                "to": null,
                                "contractAddress": null,
                                "logs": [],
                                "logsBloom": "0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
                                "status": "0x0",
                                "type": "0x7e",
                                "depositReceiptVersion": "0x1",
                            },
                            "id":67
                         }
                         """;
        Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse(expected)).Using(JToken.EqualityComparer));
    }
}

internal static class TestRpcBlockchainExt
{
    public static TestRpcBlockchain.Builder<TestRpcBlockchain> WithOptimismEthRpcModule(
        this TestRpcBlockchain.Builder<TestRpcBlockchain> @this,
        IJsonRpcClient? sequencerRpcClient,
        IAccountStateProvider accountStateProvider,
        IEthereumEcdsa ecdsa,
        ITxSealer sealer,
        IOptimismSpecHelper opSpecHelper) =>
        @this.WithEthRpcModule(blockchain => new OptimismEthRpcModule(
            blockchain.RpcConfig,
            blockchain.Bridge,
            blockchain.BlockFinder,
            blockchain.BlockTree,
            blockchain.ReceiptFinder,
            blockchain.StateReader,
            blockchain.TxPool,
            blockchain.TxSender,
            blockchain.TestWallet,
            LimboLogs.Instance,
            blockchain.SpecProvider,
            blockchain.GasPriceOracle,
            new EthSyncingInfo(blockchain.BlockTree, Substitute.For<ISyncPointers>(), new SyncConfig(),
                new StaticSelector(SyncMode.All), Substitute.For<ISyncProgressResolver>(), blockchain.LogManager),
            blockchain.FeeHistoryOracle ??
            new FeeHistoryOracle(blockchain.BlockTree, blockchain.ReceiptStorage, blockchain.SpecProvider),
            blockchain.ProtocolsManager,
            blockchain.ForkInfo,
            new BlocksConfig().SecondsPerSlot,
            sequencerRpcClient, ecdsa, sealer, new LogIndexConfig(), opSpecHelper,
            new HeadBlockSignal(blockchain.BlockTree),
            new EthCapabilitiesProvider(
                blockchain.BlockTree.AsReadOnly(),
                blockchain.WorldStateManager,
                blockchain.Container.Resolve<ISyncConfig>(),
                Substitute.For<ISyncPointers>(),
                Substitute.For<IHistoryConfig>(),
                Substitute.For<IHistoryPruner>())
        ));
}
