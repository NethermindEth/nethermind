// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Json;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.Optimism.Rpc;
using Nethermind.Serialization.Rlp;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.TxPool;
using Newtonsoft.Json.Linq;
using NSubstitute;
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

    [Test]
    public async Task Sequencer_send_transaction_with_signature_will_not_try_to_sign()
    {
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        ITxSender txSender = Substitute.For<ITxSender>();
        txSender.SendTransaction(tx: Arg.Any<Transaction>(), txHandlingOptions: TxHandlingOptions.PersistentBroadcast)
            .Returns(returnThis: (TestItem.KeccakA, AcceptTxResult.Accepted));

        EthereumEcdsa ethereumEcdsa = new EthereumEcdsa(chainId: TestBlockchainIds.ChainId);
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
        serialized.Should().BeEquivalentTo($$"""{"jsonrpc":"2.0","result":"{{TestItem.KeccakA.Bytes.ToHexString(withZeroX: true)}}","id":67}""");
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
        bridge.GetTransaction(TestItem.KeccakA, checkTxnPool: true).Returns((receipt, tx, (UInt256?)0));

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
        var expected = $$"""
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
                                 "transactionIndex": "0x20"
                             },
                            "id":67
                         }
                         """;
        JToken.Parse(serialized).Should().BeEquivalentTo(expected);
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
        bridge.GetTransaction(TestItem.KeccakA, checkTxnPool: true).Returns((receipt, tx, (UInt256?)0));

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
        var expected = $$"""
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
                                 "transactionIndex": "0x20"
                             },
                            "id":67
                         }
                         """;
        JToken.Parse(serialized).Should().BeEquivalentTo(expected);
    }

    [Test]
    public async Task GetTransactionByBlockAndIndex_ReturnsCorrectTransactionType()
    {
        Transaction tx = Build.A.Transaction
            .WithType(TxType.Legacy)
            .WithHash(TestItem.KeccakA)
            .WithSenderAddress(TestItem.AddressA)
            .TestObject;
        OptimismTxReceipt receipt = new()
        {
            TxHash = tx.Hash!,
            BlockHash = TestItem.KeccakB,
        };
        Block block = Build.A.Block
            .WithHeader(Build.A.BlockHeader
                .WithHash(TestItem.KeccakC)
                .WithNumber(123)
                .TestObject)
            .WithTransactions(tx)
            .TestObject;

        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.FindBlock(new BlockParameter(block.Hash!)).Returns(block);
        blockFinder.FindBlock(new BlockParameter(block.Number)).Returns(block);

        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();
        receiptFinder.Get(block).Returns([receipt]);

        TestRpcBlockchain rpcBlockchain = await TestRpcBlockchain
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

        var expected = $$"""
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
                                "blockNumber": null,
                                "transactionIndex": null
                            },
                            "id":67
                         }
                         """;
        {
            // By block hash
            string serialized = await rpcBlockchain.TestEthRpc("eth_getTransactionByBlockHashAndIndex", block.Hash, 0);
            JToken.Parse(serialized).Should().BeEquivalentTo(expected);
        }
        {
            // By block number
            string serialized = await rpcBlockchain.TestEthRpc("eth_getTransactionByBlockNumberAndIndex", block.Number, 0);
            JToken.Parse(serialized).Should().BeEquivalentTo(expected);
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
        OptimismTxReceipt receipt = new()
        {
            TxHash = tx.Hash!,
            BlockHash = TestItem.KeccakB,
            DepositReceiptVersion = 0x20,
        };
        Block block = Build.A.Block
            .WithHeader(Build.A.BlockHeader
                .WithHash(TestItem.KeccakC)
                .WithNumber(123)
                .TestObject)
            .WithTransactions(tx)
            .TestObject;

        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.FindBlock(new BlockParameter(block.Hash!)).Returns(block);
        blockFinder.FindBlock(new BlockParameter(block.Number)).Returns(block);

        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();
        receiptFinder.Get(block).Returns([receipt]);

        TestRpcBlockchain rpcBlockchain = await TestRpcBlockchain
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

        var expected = $$"""
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
                                 "depositReceiptVersion": "0x20",
                                 "hash": "{{tx.Hash!.Bytes.ToHexString(withZeroX: true)}}",
                                 "blockHash": "{{block.Hash!.Bytes.ToHexString(withZeroX: true)}}",
                                 "blockNumber": null,
                                 "transactionIndex": null
                             },
                            "id":67
                         }
                         """;
        {
            // By block hash
            string serialized = await rpcBlockchain.TestEthRpc("eth_getTransactionByBlockHashAndIndex", block.Hash, 0);
            JToken.Parse(serialized).Should().BeEquivalentTo(expected);
        }
        {
            // By block number
            string serialized = await rpcBlockchain.TestEthRpc("eth_getTransactionByBlockNumberAndIndex", block.Number, 0);
            JToken.Parse(serialized).Should().BeEquivalentTo(expected);
        }
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
        IOptimismSpecHelper opSpecHelper)
    {
        return @this.WithEthRpcModule(blockchain => new OptimismEthRpcModule(
            blockchain.RpcConfig,
            blockchain.Bridge,
            blockchain.BlockFinder,
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
            new BlocksConfig().SecondsPerSlot,

            sequencerRpcClient, ecdsa, sealer, opSpecHelper
        ));
    }
}
