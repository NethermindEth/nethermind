// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Proof;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using NUnit.Framework;
using System.Threading.Tasks;
using Nethermind.Consensus.Processing;
using NSubstitute;

namespace Nethermind.JsonRpc.Test.Modules.Proof
{
    [Parallelizable(ParallelScope.None)]
    [TestFixture(true, true)]
    [TestFixture(true, false)]
    [TestFixture(false, false)]
    public class ProofRpcModuleTests
    {
        private readonly bool _createSystemAccount;
        private readonly bool _useNonZeroGasPrice;
        private IProofRpcModule _proofRpcModule;
        private IBlockTree _blockTree;
        private IDbProvider _dbProvider;
        private TestSpecProvider _specProvider;

        public ProofRpcModuleTests(bool createSystemAccount, bool useNonZeroGasPrice)
        {
            _createSystemAccount = createSystemAccount;
            _useNonZeroGasPrice = useNonZeroGasPrice;
        }

        [SetUp]
        public async Task Setup()
        {
            InMemoryReceiptStorage receiptStorage = new();
            _specProvider = new TestSpecProvider(London.Instance);
            _blockTree = Build.A.BlockTree(_specProvider).WithTransactions(receiptStorage).OfChainLength(10).TestObject;
            _dbProvider = await TestMemDbProvider.InitAsync();

            ProofModuleFactory moduleFactory = new(
                _dbProvider,
                _blockTree,
                new TrieStore(_dbProvider.StateDb, LimboLogs.Instance).AsReadOnly(),
                new CompositeBlockPreprocessorStep(new RecoverSignatures(new EthereumEcdsa(TestBlockchainIds.ChainId, LimboLogs.Instance), NullTxPool.Instance, _specProvider, LimboLogs.Instance)),
                receiptStorage,
                _specProvider,
                LimboLogs.Instance);

            _proofRpcModule = moduleFactory.Create();
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Can_get_transaction(bool withHeader)
        {
            Keccak txHash = _blockTree.FindBlock(1).Transactions[0].Hash;
            TransactionWithProof txWithProof = _proofRpcModule.proof_getTransactionByHash(txHash, withHeader).Data;
            Assert.NotNull(txWithProof.Transaction);
            Assert.AreEqual(2, txWithProof.TxProof.Length);
            if (withHeader)
            {
                Assert.NotNull(txWithProof.BlockHeader);
            }
            else
            {
                Assert.Null(txWithProof.BlockHeader);
            }

            string response = RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionByHash", $"{txHash}", $"{withHeader}");
            Assert.True(response.Contains("\"result\""));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void When_getting_non_existing_tx_correct_error_code_is_returned(bool withHeader)
        {
            Keccak txHash = TestItem.KeccakH;
            string response = RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionByHash", $"{txHash}", $"{withHeader}");
            Assert.True(response.Contains($"{ErrorCodes.ResourceNotFound}"));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void When_getting_non_existing_receipt_correct_error_code_is_returned(bool withHeader)
        {
            Keccak txHash = TestItem.KeccakH;
            string response = RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionReceipt", $"{txHash}", $"{withHeader}");
            Assert.True(response.Contains($"{ErrorCodes.ResourceNotFound}"));
        }

        [Test]
        public void On_incorrect_params_returns_correct_error_code()
        {
            Keccak txHash = TestItem.KeccakH;

            // missing with header
            string response = RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionReceipt", $"{txHash}");
            Assert.True(response.Contains($"{ErrorCodes.InvalidParams}"), "missing");

            // too many
            response = RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionReceipt", $"{txHash}", "true", "false");
            Assert.True(response.Contains($"{ErrorCodes.InvalidParams}"), "too many");

            // missing with header
            response = RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionByHash", $"{txHash}");
            Assert.True(response.Contains($"{ErrorCodes.InvalidParams}"), "missing");

            // too many
            response = RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionByHash", $"{txHash}", "true", "false");
            Assert.True(response.Contains($"{ErrorCodes.InvalidParams}"), "too many");

            // all wrong
            response = RpcTest.TestSerializedRequest(_proofRpcModule, "proof_call", $"{txHash}");
            Assert.True(response.Contains($"{ErrorCodes.InvalidParams}"), "missing");
        }

        [TestCase(true, "{\"jsonrpc\":\"2.0\",\"result\":{\"receipt\":{\"transactionHash\":\"0x6db23e4d6e1f23a0f67ae8637cd675363ec59aea22acd86300ac1f1cb42c9011\",\"transactionIndex\":\"0x0\",\"blockHash\":\"0x77f368c23226eee1583f671719f117df588fc5bf19c2a73e190e404a8be570f1\",\"blockNumber\":\"0x1\",\"cumulativeGasUsed\":\"0x0\",\"gasUsed\":\"0x0\",\"effectiveGasPrice\":\"0x1\",\"to\":null,\"contractAddress\":null,\"logs\":[],\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"status\":\"0x0\",\"type\":\"0x0\"},\"txProof\":[\"0xf851a0e244ea69b68d9f3fd5eff812a4a7e1e105a8c1143ff82206458ad45fe1801c9b80808080808080a08a1641bd871a8d574e81653362ae89e549a9ab0660bd5b180328d00f13e9c6bb8080808080808080\",\"0xf86530b862f860800182520894000000000000000000000000000000000000000001818025a0e7b18371f1b94890bd11e7f67ba7e7a3a6b263d68b2d18e258f6e063d6abd90ea00a015b31944dee0bde211cec1636a3f05bfea0678e240ae8dfe309b2aac22d93\"],\"receiptProof\":[\"0xf851a053e4a8d7d8438fa45d6b75bbd6fb699b08049c1caf1c21ada42a746ddfb61d0b80808080808080a04de834bd23b53a3d82923ae5f359239b326c66758f2ae636ab934844dba2b9658080808080808080\",\"0xf9010f30b9010bf9010880825208b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c0\"],\"blockHeader\":\"0xf901f9a0b3157bcccab04639f6393042690a6c9862deebe88c781f911e8dfd265531e9ffa01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a038b96dec209c13afedbb48916f68cb38a423d13c469f5f1e338ad7415c9cf5e3a0e1b1585a222beceb3887dc6701802facccf186c2d0f6aa69e26ae0c431fc2b5db9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000830f424001833d090080830f424183010203a02ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e28800000000000003e8\"},\"id\":67}")]
        [TestCase(false, "{\"jsonrpc\":\"2.0\",\"result\":{\"receipt\":{\"transactionHash\":\"0x6db23e4d6e1f23a0f67ae8637cd675363ec59aea22acd86300ac1f1cb42c9011\",\"transactionIndex\":\"0x0\",\"blockHash\":\"0x77f368c23226eee1583f671719f117df588fc5bf19c2a73e190e404a8be570f1\",\"blockNumber\":\"0x1\",\"cumulativeGasUsed\":\"0x0\",\"gasUsed\":\"0x0\",\"effectiveGasPrice\":\"0x1\",\"to\":null,\"contractAddress\":null,\"logs\":[],\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"status\":\"0x0\",\"type\":\"0x0\"},\"txProof\":[\"0xf851a0e244ea69b68d9f3fd5eff812a4a7e1e105a8c1143ff82206458ad45fe1801c9b80808080808080a08a1641bd871a8d574e81653362ae89e549a9ab0660bd5b180328d00f13e9c6bb8080808080808080\",\"0xf86530b862f860800182520894000000000000000000000000000000000000000001818025a0e7b18371f1b94890bd11e7f67ba7e7a3a6b263d68b2d18e258f6e063d6abd90ea00a015b31944dee0bde211cec1636a3f05bfea0678e240ae8dfe309b2aac22d93\"],\"receiptProof\":[\"0xf851a053e4a8d7d8438fa45d6b75bbd6fb699b08049c1caf1c21ada42a746ddfb61d0b80808080808080a04de834bd23b53a3d82923ae5f359239b326c66758f2ae636ab934844dba2b9658080808080808080\",\"0xf9010f30b9010bf9010880825208b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c0\"]},\"id\":67}")]
        public void Can_get_receipt(bool withHeader, string expectedResult)
        {
            Keccak txHash = _blockTree.FindBlock(1).Transactions[0].Hash;
            ReceiptWithProof receiptWithProof = _proofRpcModule.proof_getTransactionReceipt(txHash, withHeader).Data;
            Assert.NotNull(receiptWithProof.Receipt);
            Assert.AreEqual(2, receiptWithProof.ReceiptProof.Length);

            if (withHeader)
            {
                Assert.NotNull(receiptWithProof.BlockHeader);
            }
            else
            {
                Assert.Null(receiptWithProof.BlockHeader);
            }

            string response = RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionReceipt", $"{txHash}", $"{withHeader}");
            Assert.AreEqual(expectedResult, response);
        }

        [TestCase(true, "{\"jsonrpc\":\"2.0\",\"result\":{\"receipt\":{\"transactionHash\":\"0x1d4bacd3b4db06677ec7f43b6be43a6c1c4285ba7c8e2e63021b53701cf8189b\",\"transactionIndex\":\"0x1\",\"blockHash\":\"0x77f368c23226eee1583f671719f117df588fc5bf19c2a73e190e404a8be570f1\",\"blockNumber\":\"0x1\",\"cumulativeGasUsed\":\"0x7d0\",\"gasUsed\":\"0x3e8\",\"effectiveGasPrice\":\"0x1\",\"from\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"to\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"contractAddress\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"logs\":[{\"removed\":false,\"logIndex\":\"0x2\",\"transactionIndex\":\"0x1\",\"transactionHash\":\"0x1d4bacd3b4db06677ec7f43b6be43a6c1c4285ba7c8e2e63021b53701cf8189b\",\"blockHash\":\"0x77f368c23226eee1583f671719f117df588fc5bf19c2a73e190e404a8be570f1\",\"blockNumber\":\"0x1\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]},{\"removed\":false,\"logIndex\":\"0x3\",\"transactionIndex\":\"0x1\",\"transactionHash\":\"0x1d4bacd3b4db06677ec7f43b6be43a6c1c4285ba7c8e2e63021b53701cf8189b\",\"blockHash\":\"0x77f368c23226eee1583f671719f117df588fc5bf19c2a73e190e404a8be570f1\",\"blockNumber\":\"0x1\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]}],\"logsBloom\":\"0x00000000000000000080000000000000000000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000800000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000000000\",\"status\":\"0x0\",\"type\":\"0x0\"},\"txProof\":[\"0xf851a0e244ea69b68d9f3fd5eff812a4a7e1e105a8c1143ff82206458ad45fe1801c9b80808080808080a08a1641bd871a8d574e81653362ae89e549a9ab0660bd5b180328d00f13e9c6bb8080808080808080\",\"0xf86431b861f85f8001825208940000000000000000000000000000000000000000020125a00861eb73c37c3560fc40047523506de00ecfa6b96dff7d37e5ce75dc3986078da032e161403eae434b0f94a36fcc7e6ad46ccffc00fe90f0756118506e918eaef9\"],\"receiptProof\":[\"0xf851a053e4a8d7d8438fa45d6b75bbd6fb699b08049c1caf1c21ada42a746ddfb61d0b80808080808080a04de834bd23b53a3d82923ae5f359239b326c66758f2ae636ab934844dba2b9658080808080808080\",\"0xf9010f31b9010bf901088082a410b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c0\"],\"blockHeader\":\"0xf901f9a0b3157bcccab04639f6393042690a6c9862deebe88c781f911e8dfd265531e9ffa01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a038b96dec209c13afedbb48916f68cb38a423d13c469f5f1e338ad7415c9cf5e3a0e1b1585a222beceb3887dc6701802facccf186c2d0f6aa69e26ae0c431fc2b5db9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000830f424001833d090080830f424183010203a02ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e28800000000000003e8\"},\"id\":67}")]
        [TestCase(false, "{\"jsonrpc\":\"2.0\",\"result\":{\"receipt\":{\"transactionHash\":\"0x1d4bacd3b4db06677ec7f43b6be43a6c1c4285ba7c8e2e63021b53701cf8189b\",\"transactionIndex\":\"0x1\",\"blockHash\":\"0x77f368c23226eee1583f671719f117df588fc5bf19c2a73e190e404a8be570f1\",\"blockNumber\":\"0x1\",\"cumulativeGasUsed\":\"0x7d0\",\"gasUsed\":\"0x3e8\",\"effectiveGasPrice\":\"0x1\",\"from\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"to\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"contractAddress\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"logs\":[{\"removed\":false,\"logIndex\":\"0x2\",\"transactionIndex\":\"0x1\",\"transactionHash\":\"0x1d4bacd3b4db06677ec7f43b6be43a6c1c4285ba7c8e2e63021b53701cf8189b\",\"blockHash\":\"0x77f368c23226eee1583f671719f117df588fc5bf19c2a73e190e404a8be570f1\",\"blockNumber\":\"0x1\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]},{\"removed\":false,\"logIndex\":\"0x3\",\"transactionIndex\":\"0x1\",\"transactionHash\":\"0x1d4bacd3b4db06677ec7f43b6be43a6c1c4285ba7c8e2e63021b53701cf8189b\",\"blockHash\":\"0x77f368c23226eee1583f671719f117df588fc5bf19c2a73e190e404a8be570f1\",\"blockNumber\":\"0x1\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]}],\"logsBloom\":\"0x00000000000000000080000000000000000000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000800000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000000000\",\"status\":\"0x0\",\"type\":\"0x0\"},\"txProof\":[\"0xf851a0e244ea69b68d9f3fd5eff812a4a7e1e105a8c1143ff82206458ad45fe1801c9b80808080808080a08a1641bd871a8d574e81653362ae89e549a9ab0660bd5b180328d00f13e9c6bb8080808080808080\",\"0xf86431b861f85f8001825208940000000000000000000000000000000000000000020125a00861eb73c37c3560fc40047523506de00ecfa6b96dff7d37e5ce75dc3986078da032e161403eae434b0f94a36fcc7e6ad46ccffc00fe90f0756118506e918eaef9\"],\"receiptProof\":[\"0xf851a053e4a8d7d8438fa45d6b75bbd6fb699b08049c1caf1c21ada42a746ddfb61d0b80808080808080a04de834bd23b53a3d82923ae5f359239b326c66758f2ae636ab934844dba2b9658080808080808080\",\"0xf9010f31b9010bf901088082a410b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c0\"]},\"id\":67}")]
        public void Get_receipt_when_block_has_few_receipts(bool withHeader, string expectedResult)
        {
            IReceiptFinder _receiptFinder = Substitute.For<IReceiptFinder>();
            LogEntry[] logEntries = new[] { Build.A.LogEntry.TestObject, Build.A.LogEntry.TestObject };

            TxReceipt receipt1 = new TxReceipt()
            {
                Bloom = new Bloom(logEntries),
                Index = 0,
                Recipient = TestItem.AddressA,
                Sender = TestItem.AddressB,
                BlockHash = _blockTree.FindBlock(1).Hash,
                BlockNumber = 1,
                ContractAddress = TestItem.AddressC,
                GasUsed = 1000,
                TxHash = _blockTree.FindBlock(1).Transactions[0].Hash,
                StatusCode = 0,
                GasUsedTotal = 2000,
                Logs = logEntries
            };

            TxReceipt receipt2 = new TxReceipt()
            {
                Bloom = new Bloom(logEntries),
                Index = 1,
                Recipient = TestItem.AddressC,
                Sender = TestItem.AddressD,
                BlockHash = _blockTree.FindBlock(1).Hash,
                BlockNumber = 1,
                ContractAddress = TestItem.AddressC,
                GasUsed = 1000,
                TxHash = _blockTree.FindBlock(1).Transactions[1].Hash,
                StatusCode = 0,
                GasUsedTotal = 2000,
                Logs = logEntries
            };

            Block block = _blockTree.FindBlock(1);
            Keccak txHash = _blockTree.FindBlock(1).Transactions[1].Hash;
            TxReceipt[] receipts = { receipt1, receipt2 };
            _receiptFinder.Get(Arg.Any<Block>()).Returns(receipts);
            _receiptFinder.Get(Arg.Any<Keccak>()).Returns(receipts);
            _receiptFinder.FindBlockHash(Arg.Any<Keccak>()).Returns(_blockTree.FindBlock(1).Hash);

            ProofModuleFactory moduleFactory = new ProofModuleFactory(
                _dbProvider,
                _blockTree,
                new TrieStore(_dbProvider.StateDb, LimboLogs.Instance).AsReadOnly(),
                new CompositeBlockPreprocessorStep(new RecoverSignatures(new EthereumEcdsa(TestBlockchainIds.ChainId, LimboLogs.Instance), NullTxPool.Instance, _specProvider, LimboLogs.Instance)),
                _receiptFinder,
                _specProvider,
                LimboLogs.Instance);

            _proofRpcModule = moduleFactory.Create();
            ReceiptWithProof receiptWithProof = _proofRpcModule.proof_getTransactionReceipt(txHash, withHeader).Data;

            if (withHeader)
            {
                Assert.NotNull(receiptWithProof.BlockHeader);
            }
            else
            {
                Assert.Null(receiptWithProof.BlockHeader);
            }

            string response = RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionReceipt", $"{txHash}", $"{withHeader}");
            Assert.AreEqual(expectedResult, response);
        }

        [Test]
        public void Can_call()
        {
            StateProvider stateProvider = CreateInitialState(null);

            Keccak root = stateProvider.StateRoot;
            Block block = Build.A.Block.WithParent(_blockTree.Head).WithStateRoot(root).TestObject;
            BlockTreeBuilder.AddBlock(_blockTree, block);

            // would need to setup state root somehow...

            TransactionForRpc tx = new()
            {
                From = TestItem.AddressA,
                To = TestItem.AddressB,
                GasPrice = _useNonZeroGasPrice ? 10.GWei() : 0
            };

            _proofRpcModule.proof_call(tx, new BlockParameter(block.Number));

            EthereumJsonSerializer serializer = new();
            string response = RpcTest.TestSerializedRequest(_proofRpcModule, "proof_call", $"{serializer.Serialize(tx)}", $"{block.Number}");
            Assert.True(response.Contains("\"result\""));
        }

        [Test]
        public void Can_call_by_hash()
        {
            StateProvider stateProvider = CreateInitialState(null);

            Keccak root = stateProvider.StateRoot;
            Block block = Build.A.Block.WithParent(_blockTree.Head).WithStateRoot(root).TestObject;
            BlockTreeBuilder.AddBlock(_blockTree, block);

            // would need to setup state root somehow...

            TransactionForRpc tx = new()
            {
                From = TestItem.AddressA,
                To = TestItem.AddressB,
                GasPrice = _useNonZeroGasPrice ? 10.GWei() : 0
            };
            _proofRpcModule.proof_call(tx, new BlockParameter(block.Hash));

            EthereumJsonSerializer serializer = new();
            string response = RpcTest.TestSerializedRequest(_proofRpcModule, "proof_call", $"{serializer.Serialize(tx)}", $"{block.Hash}");
            Assert.True(response.Contains("\"result\""));
        }

        [Test]
        public void Can_call_by_hash_canonical()
        {
            Block lastHead = _blockTree.Head;
            Block block = Build.A.Block.WithParent(lastHead).TestObject;
            Block newBlockOnMain = Build.A.Block.WithParent(lastHead).WithDifficulty(block.Difficulty + 1).TestObject;
            BlockTreeBuilder.AddBlock(_blockTree, block);
            BlockTreeBuilder.AddBlock(_blockTree, newBlockOnMain);

            // would need to setup state root somehow...

            TransactionForRpc tx = new()
            {
                From = TestItem.AddressA,
                To = TestItem.AddressB,
                GasPrice = _useNonZeroGasPrice ? 10.GWei() : 0
            };

            EthereumJsonSerializer serializer = new();
            string response = RpcTest.TestSerializedRequest(_proofRpcModule, "proof_call", $"{serializer.Serialize(tx)}", $"{{\"blockHash\" : \"{block.Hash}\", \"requireCanonical\" : true}}");
            Assert.True(response.Contains("-32000"));

            response = RpcTest.TestSerializedRequest(_proofRpcModule, "proof_call", $"{serializer.Serialize(tx)}", $"{{\"blockHash\" : \"{TestItem.KeccakG}\", \"requireCanonical\" : true}}");
            Assert.True(response.Contains("-32001"));
        }

        [Test]
        public void Can_call_with_block_hashes()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x01")
                .Op(Instruction.BLOCKHASH)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2, result.BlockHeaders.Length);
        }

        [Test]
        public void Can_call_with_many_block_hashes()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x01")
                .Op(Instruction.BLOCKHASH)
                .PushData("0x02")
                .Op(Instruction.BLOCKHASH)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(3, result.BlockHeaders.Length);
        }

        [Test]
        public void Can_call_with_same_block_hash_many_time()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x01")
                .Op(Instruction.BLOCKHASH)
                .PushData("0x01")
                .Op(Instruction.BLOCKHASH)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2, result.BlockHeaders.Length);
        }

        [Test]
        public void Can_call_with_storage_load()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x01")
                .Op(Instruction.SLOAD)
                .Done;

            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(1 + (_useNonZeroGasPrice ? 1 : 0), result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_many_storage_loads()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x01")
                .Op(Instruction.SLOAD)
                .PushData("0x02")
                .Op(Instruction.SLOAD)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(1 + (_useNonZeroGasPrice ? 1 : 0), result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_storage_write()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x01")
                .PushData("0x01")
                .Op(Instruction.SSTORE)
                .Done;

            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(1 + (_useNonZeroGasPrice ? 1 : 0), result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_extcodecopy()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x20")
                .PushData("0x00")
                .PushData("0x00")
                .PushData(TestItem.AddressC)
                .Op(Instruction.EXTCODECOPY)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2 + (_useNonZeroGasPrice ? 1 : 0), result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_extcodecopy_to_system_account()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x20")
                .PushData("0x00")
                .PushData("0x00")
                .PushData(Address.SystemUser)
                .Op(Instruction.EXTCODECOPY)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2, result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_extcodesize()
        {
            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .Op(Instruction.EXTCODESIZE)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2 + (_useNonZeroGasPrice ? 1 : 0), result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_extcodesize_to_system_account()
        {
            byte[] code = Prepare.EvmCode
                .PushData(Address.SystemUser)
                .Op(Instruction.EXTCODESIZE)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2, result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_extcodehash()
        {
            _specProvider.SpecToReturn = MuirGlacier.Instance;
            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .Op(Instruction.EXTCODEHASH)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2 + (_useNonZeroGasPrice ? 1 : 0), result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_extcodehash_to_system_account()
        {
            _specProvider.SpecToReturn = MuirGlacier.Instance;
            byte[] code = Prepare.EvmCode
                .PushData(Address.SystemUser)
                .Op(Instruction.EXTCODEHASH)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2, result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_just_basic_addresses()
        {
            _specProvider.SpecToReturn = MuirGlacier.Instance;
            byte[] code = Prepare.EvmCode
                .Op(Instruction.STOP)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(1 + (_useNonZeroGasPrice ? 1 : 0), result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_balance()
        {
            _specProvider.SpecToReturn = MuirGlacier.Instance;
            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .Op(Instruction.BALANCE)
                .Done;

            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2 + (_useNonZeroGasPrice ? 1 : 0), result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_self_balance()
        {
            _specProvider.SpecToReturn = MuirGlacier.Instance;
            byte[] code = Prepare.EvmCode
                .Op(Instruction.SELFBALANCE)
                .Done;

            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(1 + (_useNonZeroGasPrice ? 1 : 0), result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_balance_of_system_account()
        {
            _specProvider.SpecToReturn = MuirGlacier.Instance;
            byte[] code = Prepare.EvmCode
                .PushData(Address.SystemUser)
                .Op(Instruction.BALANCE)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2, result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_call_to_system_account_with_zero_value()
        {
            _specProvider.SpecToReturn = MuirGlacier.Instance;
            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(Address.SystemUser)
                .PushData(1000000)
                .Op(Instruction.CALL)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2, result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_static_call_to_system_account()
        {
            _specProvider.SpecToReturn = MuirGlacier.Instance;
            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(Address.SystemUser)
                .PushData(1000000)
                .Op(Instruction.STATICCALL)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2, result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_delegate_call_to_system_account()
        {
            _specProvider.SpecToReturn = MuirGlacier.Instance;
            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(Address.SystemUser)
                .PushData(1000000)
                .Op(Instruction.DELEGATECALL)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2, result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_call_to_system_account_with_non_zero_value()
        {
            _specProvider.SpecToReturn = MuirGlacier.Instance;
            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(1)
                .PushData(Address.SystemUser)
                .PushData(1000000)
                .Op(Instruction.CALL)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2, result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_call_with_zero_value()
        {
            _specProvider.SpecToReturn = MuirGlacier.Instance;
            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(TestItem.AddressC)
                .PushData(1000000)
                .Op(Instruction.CALL)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2 + (_useNonZeroGasPrice ? 1 : 0), result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_static_call()
        {
            _specProvider.SpecToReturn = MuirGlacier.Instance;
            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(TestItem.AddressC)
                .PushData(1000000)
                .Op(Instruction.STATICCALL)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2 + (_useNonZeroGasPrice ? 1 : 0), result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_delegate_call()
        {
            _specProvider.SpecToReturn = MuirGlacier.Instance;
            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(TestItem.AddressC)
                .PushData(1000000)
                .Op(Instruction.DELEGATECALL)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(3, result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_call_with_non_zero_value()
        {
            _specProvider.SpecToReturn = MuirGlacier.Instance;
            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(1)
                .PushData(TestItem.AddressC)
                .PushData(1000000)
                .Op(Instruction.CALL)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2 + (_useNonZeroGasPrice ? 1 : 0), result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_self_destruct()
        {
            _specProvider.SpecToReturn = MuirGlacier.Instance;
            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .Op(Instruction.SELFDESTRUCT)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);

            Assert.AreEqual(2 + (_useNonZeroGasPrice ? 1 : 0), result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_self_destruct_to_system_account()
        {
            _specProvider.SpecToReturn = MuirGlacier.Instance;
            byte[] code = Prepare.EvmCode
                .PushData(Address.SystemUser)
                .Op(Instruction.SELFDESTRUCT)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2, result.Accounts.Length);
        }


        [Test]
        public void Can_call_with_many_storage_writes()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x01")
                .PushData("0x01")
                .Op(Instruction.SSTORE)
                .PushData("0x02")
                .PushData("0x02")
                .Op(Instruction.SSTORE)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(1 + (_useNonZeroGasPrice ? 1 : 0), result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_mix_of_everything()
        {
            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .Op(Instruction.BALANCE)
                .PushData("0x01")
                .Op(Instruction.BLOCKHASH)
                .PushData("0x02")
                .Op(Instruction.BLOCKHASH)
                .PushData("0x01")
                .Op(Instruction.SLOAD)
                .PushData("0x02")
                .Op(Instruction.SLOAD)
                .PushData("0x01")
                .PushData("0x01")
                .Op(Instruction.SSTORE)
                .PushData("0x03")
                .PushData("0x03")
                .Op(Instruction.SSTORE)
                .Done;

            TestCallWithCode(code);
        }

        [Test]
        public void Can_call_with_mix_of_everything_and_storage()
        {
            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .Op(Instruction.BALANCE)
                .PushData("0x01")
                .Op(Instruction.BLOCKHASH)
                .PushData("0x02")
                .Op(Instruction.BLOCKHASH)
                .PushData("0x01")
                .Op(Instruction.SLOAD)
                .PushData("0x02")
                .Op(Instruction.SLOAD)
                .PushData("0x01")
                .PushData("0x01")
                .Op(Instruction.SSTORE)
                .PushData("0x03")
                .PushData("0x03")
                .Op(Instruction.SSTORE)
                .Done;

            TestCallWithStorageAndCode(code, _useNonZeroGasPrice ? 10.GWei() : 0);
        }

        [Test]
        public void Can_call_with_mix_of_everything_and_storage_from_another_account_wrong_nonce()
        {
            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .Op(Instruction.BALANCE)
                .PushData("0x01")
                .Op(Instruction.BLOCKHASH)
                .PushData("0x02")
                .Op(Instruction.BLOCKHASH)
                .PushData("0x01")
                .Op(Instruction.SLOAD)
                .PushData("0x02")
                .Op(Instruction.SLOAD)
                .PushData("0x01")
                .PushData("0x01")
                .Op(Instruction.SSTORE)
                .PushData("0x03")
                .PushData("0x03")
                .Op(Instruction.SSTORE)
                .Done;

            TestCallWithStorageAndCode(code, 0, TestItem.AddressD);
        }

        private CallResultWithProof TestCallWithCode(byte[] code, Address? from = null)
        {
            StateProvider stateProvider = CreateInitialState(code);

            Keccak root = stateProvider.StateRoot;
            Block block = Build.A.Block.WithParent(_blockTree.Head!).WithStateRoot(root).WithBeneficiary(TestItem.AddressD).TestObject;
            BlockTreeBuilder.AddBlock(_blockTree, block);
            Block blockOnTop = Build.A.Block.WithParent(block).WithStateRoot(root).WithBeneficiary(TestItem.AddressD).TestObject;
            BlockTreeBuilder.AddBlock(_blockTree, blockOnTop);

            // would need to setup state root somehow...

            TransactionForRpc tx = new()
            {
                From = from,
                To = TestItem.AddressB,
                GasPrice = _useNonZeroGasPrice ? 10.GWei() : 0
            };

            CallResultWithProof callResultWithProof = _proofRpcModule.proof_call(tx, new BlockParameter(blockOnTop.Number)).Data;
            Assert.Greater(callResultWithProof.Accounts.Length, 0);

            foreach (AccountProof accountProof in callResultWithProof.Accounts)
            {
                ProofVerifier.VerifyOneProof(accountProof.Proof!, block.StateRoot!);
                foreach (StorageProof storageProof in accountProof.StorageProofs!)
                {
                    ProofVerifier.VerifyOneProof(storageProof.Proof!, accountProof.StorageRoot);
                }
            }

            EthereumJsonSerializer serializer = new();
            string response = RpcTest.TestSerializedRequest(_proofRpcModule, "proof_call", $"{serializer.Serialize(tx)}", $"{blockOnTop.Number}");
            Assert.True(response.Contains("\"result\""));

            return callResultWithProof;
        }

        private void TestCallWithStorageAndCode(byte[] code, UInt256 gasPrice, Address? from = null)
        {
            StateProvider stateProvider = CreateInitialState(code);
            StorageProvider storageProvider = new(new TrieStore(_dbProvider.StateDb, LimboLogs.Instance), stateProvider, LimboLogs.Instance);

            for (int i = 0; i < 10000; i++)
            {
                storageProvider.Set(new StorageCell(TestItem.AddressB, (UInt256)i), i.ToBigEndianByteArray());
            }

            storageProvider.Commit();
            storageProvider.CommitTrees(0);

            stateProvider.Commit(MainnetSpecProvider.Instance.GenesisSpec, NullStateTracer.Instance);
            stateProvider.CommitTree(0);

            Keccak root = stateProvider.StateRoot;

            Block block = Build.A.Block.WithParent(_blockTree.Head!).WithStateRoot(root).TestObject;
            BlockTreeBuilder.AddBlock(_blockTree, block);
            Block blockOnTop = Build.A.Block.WithParent(block).WithStateRoot(root).TestObject;
            BlockTreeBuilder.AddBlock(_blockTree, blockOnTop);

            // would need to setup state root somehow...

            TransactionForRpc tx = new()
            {
                // we are testing system transaction here when From is null
                From = from,
                To = TestItem.AddressB,
                GasPrice = gasPrice,
                Nonce = 1000
            };

            CallResultWithProof callResultWithProof = _proofRpcModule.proof_call(tx, new BlockParameter(blockOnTop.Number)).Data;
            Assert.Greater(callResultWithProof.Accounts.Length, 0);

            // just the keys for debugging
            Span<byte> span = stackalloc byte[32];
            new UInt256(0).ToBigEndian(span);
            Keccak unused = Keccak.Compute(span);

            // just the keys for debugging
            new UInt256(1).ToBigEndian(span);
            Keccak unused1 = Keccak.Compute(span);

            // just the keys for debugging
            new UInt256(2).ToBigEndian(span);
            Keccak unused2 = Keccak.Compute(span);

            foreach (AccountProof accountProof in callResultWithProof.Accounts)
            {
                // this is here for diagnostics - so you can read what happens in the test
                // generally the account here should be consistent with the values inside the proof
                // the exception will be thrown if the account did not exist before the call
                Account account;
                try
                {
                    account = new AccountDecoder().Decode(new RlpStream(ProofVerifier.VerifyOneProof(accountProof.Proof, block.StateRoot)));
                }
                catch (Exception)
                {
                    // ignored
                }

                foreach (StorageProof storageProof in accountProof.StorageProofs)
                {
                    // we read the values here just to allow easier debugging so you can confirm that the value is same as the one in the proof and in the trie
                    byte[] value = ProofVerifier.VerifyOneProof(storageProof.Proof, accountProof.StorageRoot);
                }
            }

            EthereumJsonSerializer serializer = new();
            string response = RpcTest.TestSerializedRequest(_proofRpcModule, "proof_call", $"{serializer.Serialize(tx)}", $"{blockOnTop.Number}");
            Assert.True(response.Contains("\"result\""));
        }

        private StateProvider CreateInitialState(byte[] code)
        {
            StateProvider stateProvider = new(new TrieStore(_dbProvider.StateDb, LimboLogs.Instance), _dbProvider.CodeDb, LimboLogs.Instance);
            AddAccount(stateProvider, TestItem.AddressA, 1.Ether());
            AddAccount(stateProvider, TestItem.AddressB, 1.Ether());

            if (code is not null)
            {
                AddCode(stateProvider, TestItem.AddressB, code);
            }

            if (_createSystemAccount)
            {
                AddAccount(stateProvider, Address.SystemUser, 1.Ether());
            }

            stateProvider.CommitTree(0);

            return stateProvider;
        }

        private void AddAccount(StateProvider stateProvider, Address account, UInt256 initialBalance)
        {
            stateProvider.CreateAccount(account, initialBalance);
            stateProvider.Commit(MuirGlacier.Instance, NullStateTracer.Instance);
        }

        private void AddCode(StateProvider stateProvider, Address account, byte[] code)
        {
            Keccak codeHash = stateProvider.UpdateCode(code);
            stateProvider.UpdateCodeHash(account, codeHash, MuirGlacier.Instance);

            stateProvider.Commit(MainnetSpecProvider.Instance.GenesisSpec, NullStateTracer.Instance);
        }
    }
}
