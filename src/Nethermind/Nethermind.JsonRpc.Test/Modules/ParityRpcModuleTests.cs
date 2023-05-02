// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Parity;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Db.Blooms;
using Nethermind.KeyStore;
using Nethermind.Network;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Stats.Model;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using BlockTree = Nethermind.Blockchain.BlockTree;

namespace Nethermind.JsonRpc.Test.Modules
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class ParityRpcModuleTests
    {
        private IParityRpcModule _parityRpcModule = null!;
        private Signer _signerStore = null!;

        [SetUp]
        public void Initialize()
        {
            LimboLogs logger = LimboLogs.Instance;
            MainnetSpecProvider specProvider = MainnetSpecProvider.Instance;
            EthereumEcdsa ethereumEcdsa = new(specProvider.ChainId, logger);

            Peer peerA = SetUpPeerA();      //standard case
            Peer peerB = SetUpPeerB();      //Session is null
            Peer peerC = SetUpPeerC();      //Node is null, Caps are empty
            IPeerManager peerManager = Substitute.For<IPeerManager>();
            peerManager.ActivePeers.Returns(new List<Peer> { peerA, peerB, peerC });
            peerManager.ConnectedPeers.Returns(new List<Peer> { peerA, peerB, peerA, peerC, peerB });
            peerManager.MaxActivePeers.Returns(15);

            StateProvider stateProvider = new(new TrieStore(new MemDb(), LimboLogs.Instance), new MemDb(), LimboLogs.Instance);
            StateReader stateReader = new(new TrieStore(new MemDb(), LimboLogs.Instance), new MemDb(), LimboLogs.Instance);

            IDb blockDb = new MemDb();
            IDb headerDb = new MemDb();
            IDb blockInfoDb = new MemDb();
            IBlockTree blockTree = new BlockTree(blockDb, headerDb, blockInfoDb, new ChainLevelInfoRepository(blockInfoDb), specProvider, NullBloomStorage.Instance, LimboLogs.Instance);

            ITransactionComparerProvider transactionComparerProvider =
                new TransactionComparerProvider(specProvider, blockTree);
            TxPool.TxPool txPool = new(ethereumEcdsa, new ChainHeadInfoProvider(new FixedForkActivationChainHeadSpecProvider(specProvider), blockTree, stateProvider), new TxPoolConfig(),
                new TxValidator(specProvider.ChainId), LimboLogs.Instance, transactionComparerProvider.GetDefaultComparer());

            IReceiptStorage receiptStorage = new InMemoryReceiptStorage();

            _signerStore = new Signer(specProvider.ChainId, TestItem.PrivateKeyB, logger);
            _parityRpcModule = new ParityRpcModule(ethereumEcdsa, txPool, blockTree, receiptStorage,
                new Enode(TestItem.PublicKeyA, IPAddress.Loopback, 8545), _signerStore,
                new MemKeyStore(new[] { TestItem.PrivateKeyA }, Environment.SpecialFolder.ApplicationData.ToString()),
                MainnetSpecProvider.Instance, peerManager);

            int blockNumber = 2;
            Transaction pendingTransaction = Build.A.Transaction.Signed(ethereumEcdsa, TestItem.PrivateKeyD, false)
                .WithSenderAddress(Address.FromNumber((UInt256)blockNumber)).TestObject;
            pendingTransaction.Signature!.V = 37;
            stateProvider.CreateAccount(pendingTransaction.SenderAddress!, UInt256.UInt128MaxValue);
            txPool.SubmitTx(pendingTransaction, TxHandlingOptions.None);

            blockNumber = 1;
            Transaction transaction1 = Build.A.Transaction.Signed(ethereumEcdsa, TestItem.PrivateKeyD, false)
                .WithSenderAddress(Address.FromNumber((UInt256)blockNumber))
                .WithNonce(100).TestObject;
            transaction1.Signature!.V = 37;
            stateProvider.CreateAccount(transaction1.SenderAddress!, UInt256.UInt128MaxValue);

            var transaction2 = Build.A.Transaction.Signed(ethereumEcdsa, TestItem.PrivateKeyD, false)
                .WithSenderAddress(Address.FromNumber((UInt256)blockNumber))
                .WithNonce(120).TestObject;
            transaction2.Signature!.V = 37;

            var transaction3 = Build.A.Transaction.Signed(ethereumEcdsa, TestItem.PrivateKeyD, false)
                .WithSenderAddress(Address.FromNumber((UInt256)blockNumber))
                .WithNonce(110).TestObject;
            transaction2.Signature.V = 37;

            Block genesis = Build.A.Block.Genesis
                .WithStateRoot(new Keccak("0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f"))
                .TestObject;

            blockTree.SuggestBlock(genesis);
            blockTree.UpdateMainChain(new[] { genesis }, true);

            Block previousBlock = genesis;
            Block block = Build.A.Block.WithNumber(blockNumber).WithParent(previousBlock)
                    .WithStateRoot(new Keccak("0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f"))
                    .WithTransactions(transaction1, transaction2, transaction3)
                    .TestObject;

            blockTree.SuggestBlock(block);
            blockTree.UpdateMainChain(new[] { block }, true);

            LogEntry[] logEntries = new[] { Build.A.LogEntry.TestObject };

            TxReceipt receipt1 = new()
            {
                Bloom = new Bloom(logEntries),
                Index = 0,
                Recipient = TestItem.AddressA,
                Sender = TestItem.AddressB,
                BlockHash = TestItem.KeccakA,
                BlockNumber = 1,
                ContractAddress = TestItem.AddressC,
                GasUsed = 1000,
                TxHash = transaction1.Hash,
                StatusCode = 0,
                GasUsedTotal = 2000,
                Logs = logEntries
            };

            TxReceipt receipt2 = new()
            {
                Bloom = new Bloom(logEntries),
                Index = 1,
                Recipient = TestItem.AddressA,
                Sender = TestItem.AddressB,
                BlockHash = TestItem.KeccakA,
                BlockNumber = 1,
                ContractAddress = TestItem.AddressC,
                GasUsed = 1000,
                TxHash = transaction2.Hash,
                StatusCode = 0,
                GasUsedTotal = 2000,
                Logs = logEntries
            };

            TxReceipt receipt3 = new()
            {
                Bloom = new Bloom(logEntries),
                Index = 2,
                Recipient = TestItem.AddressA,
                Sender = TestItem.AddressB,
                BlockHash = TestItem.KeccakA,
                BlockNumber = 1,
                ContractAddress = TestItem.AddressC,
                GasUsed = 1000,
                TxHash = transaction3.Hash,
                StatusCode = 0,
                GasUsedTotal = 2000,
                Logs = logEntries
            };

            receiptStorage.Insert(block, receipt1, receipt2, receipt3);
        }

        private static Peer SetUpPeerA()
        {
            Node node = new(TestItem.PublicKeyA, "127.0.0.1", 30303, true);
            node.ClientId = "Geth/v1.9.21-stable/linux-amd64/go1.15.2";

            Peer peer = new(node);
            peer.InSession = null;
            peer.OutSession = Substitute.For<ISession>();
            peer.OutSession.RemoteNodeId.Returns(TestItem.PublicKeyA);

            IProtocolHandler protocolHandler = Substitute.For<IProtocolHandler, ISyncPeer>();
            peer.OutSession.TryGetProtocolHandler(Protocol.Eth, out Arg.Any<IProtocolHandler>()).Returns(x =>
            {
                x[1] = protocolHandler;
                return true;
            });

            byte version = 65;
            protocolHandler.ProtocolVersion.Returns(version);
            if (protocolHandler is ISyncPeer syncPeer)
            {
                UInt256 difficulty = 0x5ea4ed;
                syncPeer.TotalDifficulty.Returns(difficulty);
                syncPeer.HeadHash.Returns(TestItem.KeccakA);
            }

            IProtocolHandler p2PProtocolHandler = Substitute.For<IProtocolHandler, IP2PProtocolHandler>();
            peer.OutSession.TryGetProtocolHandler(Protocol.P2P, out Arg.Any<IProtocolHandler>()).Returns(x =>
            {
                x[1] = p2PProtocolHandler;
                return true;
            });

            if (p2PProtocolHandler is IP2PProtocolHandler p2PHandler)
            {
                p2PHandler.AgreedCapabilities.Returns(new List<Capability> { new("eth", 65), new("eth", 64) });
            }

            return peer;
        }

        private static Peer SetUpPeerB()
        {
            Node node = new(TestItem.PublicKeyB, "95.217.106.25", 22222, true);
            node.ClientId = "Geth/v1.9.26-unstable/linux-amd64/go1.15.6";

            Peer peer = new(node);
            peer.InSession = null;
            peer.OutSession = null;

            return peer;
        }

        private static Peer SetUpPeerC()
        {
            Peer peer = new(null!);
            peer.InSession = Substitute.For<ISession>();
            peer.InSession.RemoteNodeId.Returns(TestItem.PublicKeyB);

            IProtocolHandler p2PProtocolHandler = Substitute.For<IProtocolHandler, IP2PProtocolHandler>();
            peer.InSession.TryGetProtocolHandler(Protocol.P2P, out Arg.Any<IProtocolHandler>()).Returns(x =>
            {
                x[1] = p2PProtocolHandler;
                return true;
            });

            if (p2PProtocolHandler is IP2PProtocolHandler p2PHandler)
            {
                p2PHandler.AgreedCapabilities.Returns(new List<Capability> { });
            }

            return peer;
        }

        [Test]
        public async Task parity_pendingTransactions()
        {
            await Task.Delay(100);
            string serialized = RpcTest.TestSerializedRequest(_parityRpcModule, "parity_pendingTransactions");
            string expectedResult = "{\"jsonrpc\":\"2.0\",\"result\":[{\"hash\":\"0xd4720d1b81c70ed4478553a213a83bd2bf6988291677f5d05c6aae0b287f947e\",\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"from\":\"0x0000000000000000000000000000000000000002\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"input\":\"0x\",\"raw\":\"0xf85f8001825208940000000000000000000000000000000000000000018025a0ef2effb79771cbe42fc7f9cc79440b2a334eedad6e528ea45c2040789def4803a0515bdfe298808be2e07879faaeacd0ad17f3b13305b9f971647bbd5d5b584642\",\"creates\":null,\"publicKey\":\"0x15a1cc027cfd2b970c8aa2b3b22dfad04d29171109f6502d5fb5bde18afe86dddd44b9f8d561577527f096860ee03f571cc7f481ea9a14cb48cc7c20c964373a\",\"chainId\":\"0x1\",\"condition\":null,\"r\":\"0xef2effb79771cbe42fc7f9cc79440b2a334eedad6e528ea45c2040789def4803\",\"s\":\"0x515bdfe298808be2e07879faaeacd0ad17f3b13305b9f971647bbd5d5b584642\",\"v\":\"0x25\",\"standardV\":\"0x0\"}],\"id\":67}";
            Assert.That(serialized, Is.EqualTo(expectedResult));
        }

        [Test]
        public async Task parity_pendingTransactions_With_Address()
        {
            await Task.Delay(100);
            string serialized = RpcTest.TestSerializedRequest(_parityRpcModule, "parity_pendingTransactions", "0x0000000000000000000000000000000000000002");
            string expectedResult = "{\"jsonrpc\":\"2.0\",\"result\":[{\"hash\":\"0xd4720d1b81c70ed4478553a213a83bd2bf6988291677f5d05c6aae0b287f947e\",\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"from\":\"0x0000000000000000000000000000000000000002\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"input\":\"0x\",\"raw\":\"0xf85f8001825208940000000000000000000000000000000000000000018025a0ef2effb79771cbe42fc7f9cc79440b2a334eedad6e528ea45c2040789def4803a0515bdfe298808be2e07879faaeacd0ad17f3b13305b9f971647bbd5d5b584642\",\"creates\":null,\"publicKey\":\"0x15a1cc027cfd2b970c8aa2b3b22dfad04d29171109f6502d5fb5bde18afe86dddd44b9f8d561577527f096860ee03f571cc7f481ea9a14cb48cc7c20c964373a\",\"chainId\":\"0x1\",\"condition\":null,\"r\":\"0xef2effb79771cbe42fc7f9cc79440b2a334eedad6e528ea45c2040789def4803\",\"s\":\"0x515bdfe298808be2e07879faaeacd0ad17f3b13305b9f971647bbd5d5b584642\",\"v\":\"0x25\",\"standardV\":\"0x0\"}],\"id\":67}";
            Assert.That(serialized, Is.EqualTo(expectedResult));
        }

        [Test]
        public async Task parity_pendingTransactions_With_Address_Empty_Result()
        {
            await Task.Delay(100);
            string serialized = RpcTest.TestSerializedRequest(_parityRpcModule, "parity_pendingTransactions", "0x0000000000000000000000000000000000000005");
            string expectedResult = "{\"jsonrpc\":\"2.0\",\"result\":[],\"id\":67}";
            Assert.That(serialized, Is.EqualTo(expectedResult));
        }

        [Test]
        public void parity_getBlockReceipts()
        {
            string serialized = RpcTest.TestSerializedRequest(_parityRpcModule, "parity_getBlockReceipts", "latest");
            string expectedResult = "{\"jsonrpc\":\"2.0\",\"result\":[{\"transactionHash\":\"0x026217c3c4eb1f0e9e899553759b6e909b965a789c6136d256674718617c8142\",\"transactionIndex\":\"0x0\",\"blockHash\":\"0x5077d73d2e82d0b7799392db86827826f181df13e3f50fed89cbb5aa03f5230f\",\"blockNumber\":\"0x1\",\"cumulativeGasUsed\":\"0x7d0\",\"gasUsed\":\"0x3e8\",\"effectiveGasPrice\":\"0x1\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"to\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"contractAddress\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"logs\":[{\"removed\":false,\"logIndex\":\"0x0\",\"transactionIndex\":\"0x0\",\"transactionHash\":\"0x026217c3c4eb1f0e9e899553759b6e909b965a789c6136d256674718617c8142\",\"blockHash\":\"0x5077d73d2e82d0b7799392db86827826f181df13e3f50fed89cbb5aa03f5230f\",\"blockNumber\":\"0x1\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]}],\"logsBloom\":\"0x00000000000000000080000000000000000000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000800000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000000000\",\"status\":\"0x0\",\"type\":\"0x0\"},{\"transactionHash\":\"0xd0183bfd42ccd8fccb7722b108e052d12d2cf5a32a144b6a6f3a975c4d7d14a1\",\"transactionIndex\":\"0x1\",\"blockHash\":\"0x5077d73d2e82d0b7799392db86827826f181df13e3f50fed89cbb5aa03f5230f\",\"blockNumber\":\"0x1\",\"cumulativeGasUsed\":\"0x7d0\",\"gasUsed\":\"0x3e8\",\"effectiveGasPrice\":\"0x1\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"to\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"contractAddress\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"logs\":[{\"removed\":false,\"logIndex\":\"0x1\",\"transactionIndex\":\"0x1\",\"transactionHash\":\"0xd0183bfd42ccd8fccb7722b108e052d12d2cf5a32a144b6a6f3a975c4d7d14a1\",\"blockHash\":\"0x5077d73d2e82d0b7799392db86827826f181df13e3f50fed89cbb5aa03f5230f\",\"blockNumber\":\"0x1\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]}],\"logsBloom\":\"0x00000000000000000080000000000000000000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000800000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000000000\",\"status\":\"0x0\",\"type\":\"0x0\"},{\"transactionHash\":\"0xf8ab484b10dc0398f03957d1062bbe3526048b74d429f8a8c9c57fa7ac5fa436\",\"transactionIndex\":\"0x2\",\"blockHash\":\"0x5077d73d2e82d0b7799392db86827826f181df13e3f50fed89cbb5aa03f5230f\",\"blockNumber\":\"0x1\",\"cumulativeGasUsed\":\"0x7d0\",\"gasUsed\":\"0x3e8\",\"effectiveGasPrice\":\"0x1\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"to\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"contractAddress\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"logs\":[{\"removed\":false,\"logIndex\":\"0x2\",\"transactionIndex\":\"0x2\",\"transactionHash\":\"0xf8ab484b10dc0398f03957d1062bbe3526048b74d429f8a8c9c57fa7ac5fa436\",\"blockHash\":\"0x5077d73d2e82d0b7799392db86827826f181df13e3f50fed89cbb5aa03f5230f\",\"blockNumber\":\"0x1\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]}],\"logsBloom\":\"0x00000000000000000080000000000000000000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000800000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000000000\",\"status\":\"0x0\",\"type\":\"0x0\"}],\"id\":67}";
            Assert.That(serialized, Is.EqualTo(expectedResult));
        }

        [Test]
        public void parity_enode()
        {
            string serialized = RpcTest.TestSerializedRequest(_parityRpcModule, "parity_enode");
            string expectedResult = "{\"jsonrpc\":\"2.0\",\"result\":\"enode://a49ac7010c2e0a444dfeeabadbafa4856ba4a2d732acb86d20c577b3b365fdaeb0a70ce47f890cf2f9fca562a7ed784f76eb870a2c75c0f2ab476a70ccb67e92@127.0.0.1:8545\",\"id\":67}";
            Assert.That(serialized, Is.EqualTo(expectedResult));
        }

        [Test]
        public void parity_setEngineSigner()
        {
            string serialized = RpcTest.TestSerializedRequest(_parityRpcModule, "parity_setEngineSigner", TestItem.AddressA.ToString(), "password");
            string expectedResult = "{\"jsonrpc\":\"2.0\",\"result\":true,\"id\":67}";
            Assert.That(serialized, Is.EqualTo(expectedResult));
            _signerStore.Address.Should().Be(TestItem.AddressA);
            _signerStore.CanSign.Should().BeTrue();
        }

        [Test]
        public void parity_setEngineSignerSecret()
        {
            string serialized = RpcTest.TestSerializedRequest(_parityRpcModule, "parity_setEngineSignerSecret", TestItem.PrivateKeyA.ToString());
            string expectedResult = "{\"jsonrpc\":\"2.0\",\"result\":true,\"id\":67}";
            Assert.That(serialized, Is.EqualTo(expectedResult));
            _signerStore.Address.Should().Be(TestItem.AddressA);
            _signerStore.CanSign.Should().BeTrue();
        }

        [Test]
        public void parity_clearEngineSigner()
        {
            RpcTest.TestSerializedRequest(_parityRpcModule, "parity_setEngineSigner", TestItem.AddressA.ToString(), "password");
            string serialized = RpcTest.TestSerializedRequest(_parityRpcModule, "parity_clearEngineSigner");
            string expectedResult = "{\"jsonrpc\":\"2.0\",\"result\":true,\"id\":67}";
            serialized.Should().Be(expectedResult);
            _signerStore.Address.Should().Be(Address.Zero);
            _signerStore.CanSign.Should().BeFalse();
        }

        [Test]
        public void parity_netPeers_standard_case()
        {
            string serialized = RpcTest.TestSerializedRequest(_parityRpcModule, "parity_netPeers");
            string expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":{\"active\":3,\"connected\":5,\"max\":15,\"peers\":[{\"id\":\"", TestItem.PublicKeyA, "\",\"name\":\"Geth/v1.9.21-stable/linux-amd64/go1.15.2\",\"caps\":[\"eth/65\",\"eth/64\"],\"network\":{\"localAddress\":\"127.0.0.1\",\"remoteAddress\":\"Handshake\"},\"protocols\":{\"eth\":{\"version\":65,\"difficulty\":\"0x5ea4ed\",\"head\":\"", TestItem.KeccakA, "\"}}},{\"name\":\"Geth/v1.9.26-unstable/linux-amd64/go1.15.6\",\"caps\":[],\"network\":{\"localAddress\":\"95.217.106.25\"},\"protocols\":{\"eth\":{\"version\":0,\"difficulty\":\"0x0\"}}},{\"id\":\"", TestItem.PublicKeyB, "\",\"caps\":[],\"network\":{\"remoteAddress\":\"Handshake\"},\"protocols\":{\"eth\":{\"version\":0,\"difficulty\":\"0x0\"}}}]},\"id\":67}");
            Assert.That(serialized, Is.EqualTo(expectedResult));
        }

        [Test]
        public void parity_netPeers_empty_ActivePeers()
        {
            LimboLogs logger = LimboLogs.Instance;
            MainnetSpecProvider specProvider = MainnetSpecProvider.Instance;
            EthereumEcdsa ethereumEcdsa = new(specProvider.ChainId, logger);
            IDb blockDb = new MemDb();
            IDb headerDb = new MemDb();
            IDb blockInfoDb = new MemDb();
            IBlockTree blockTree = new BlockTree(blockDb, headerDb, blockInfoDb, new ChainLevelInfoRepository(blockInfoDb), specProvider, NullBloomStorage.Instance, LimboLogs.Instance);

            ITransactionComparerProvider transactionComparerProvider =
                new TransactionComparerProvider(specProvider, blockTree);
            TxPool.TxPool txPool = new(ethereumEcdsa, new ChainHeadInfoProvider(new FixedForkActivationChainHeadSpecProvider(specProvider), blockTree, new StateProvider(new TrieStore(new MemDb(), LimboLogs.Instance), new MemDb(), LimboLogs.Instance)), new TxPoolConfig(),
                new TxValidator(specProvider.ChainId), LimboLogs.Instance, transactionComparerProvider.GetDefaultComparer());

            IReceiptStorage receiptStorage = new InMemoryReceiptStorage();

            IPeerManager peerManager = Substitute.For<IPeerManager>();
            peerManager.ActivePeers.Returns(new List<Peer> { });
            peerManager.ConnectedPeers.Returns(new List<Peer> { new(new Node(TestItem.PublicKeyA, "111.1.1.1", 11111, true)) });

            IParityRpcModule parityRpcModule = new ParityRpcModule(ethereumEcdsa, txPool, blockTree, receiptStorage,
                new Enode(TestItem.PublicKeyA, IPAddress.Loopback, 8545),
                _signerStore, new MemKeyStore(new[] { TestItem.PrivateKeyA }, Path.Combine("testKeyStoreDir", Path.GetRandomFileName())),
                MainnetSpecProvider.Instance, peerManager);

            string serialized = RpcTest.TestSerializedRequest(parityRpcModule, "parity_netPeers");
            string expectedResult = "{\"jsonrpc\":\"2.0\",\"result\":{\"active\":0,\"connected\":1,\"max\":0,\"peers\":[]},\"id\":67}";
            Assert.That(serialized, Is.EqualTo(expectedResult));
        }

        [Test]
        public void parity_netPeers_null_ActivePeers()
        {
            LimboLogs logger = LimboLogs.Instance;
            MainnetSpecProvider specProvider = MainnetSpecProvider.Instance;
            EthereumEcdsa ethereumEcdsa = new(specProvider.ChainId, logger);

            IDb blockDb = new MemDb();
            IDb headerDb = new MemDb();
            IDb blockInfoDb = new MemDb();
            IBlockTree blockTree = new BlockTree(blockDb, headerDb, blockInfoDb, new ChainLevelInfoRepository(blockInfoDb), specProvider, NullBloomStorage.Instance, LimboLogs.Instance);
            ITransactionComparerProvider transactionComparerProvider =
                new TransactionComparerProvider(specProvider, blockTree);
            TxPool.TxPool txPool = new(ethereumEcdsa, new ChainHeadInfoProvider(specProvider, blockTree, new StateReader(new TrieStore(new MemDb(), LimboLogs.Instance), new MemDb(), LimboLogs.Instance)), new TxPoolConfig(),
                new TxValidator(specProvider.ChainId), LimboLogs.Instance, transactionComparerProvider.GetDefaultComparer());

            IReceiptStorage receiptStorage = new InMemoryReceiptStorage();

            IPeerManager peerManager = Substitute.For<IPeerManager>();

            IParityRpcModule parityRpcModule = new ParityRpcModule(ethereumEcdsa, txPool, blockTree, receiptStorage,
                new Enode(TestItem.PublicKeyA, IPAddress.Loopback, 8545),
                _signerStore, new MemKeyStore(new[] { TestItem.PrivateKeyA }, Path.Combine("testKeyStoreDir", Path.GetRandomFileName())),
                MainnetSpecProvider.Instance, peerManager);
            string serialized = RpcTest.TestSerializedRequest(parityRpcModule, "parity_netPeers");
            string expectedResult = "{\"jsonrpc\":\"2.0\",\"result\":{\"active\":0,\"connected\":0,\"max\":0,\"peers\":[]},\"id\":67}";
            Assert.That(serialized, Is.EqualTo(expectedResult));
        }
    }
}
