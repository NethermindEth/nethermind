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
using Nethermind.JsonRpc.Modules.Proof;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.TxPool;
using NUnit.Framework;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Buffers;
using Nethermind.Core.Test;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.State.Tracing;
using NSubstitute;

namespace Nethermind.JsonRpc.Test.Modules.Proof;

[Parallelizable(ParallelScope.None)]
// [TestFixture(true, true)] TODO fix or remove test?
[TestFixture(true, false)]
[TestFixture(false, false)]
public class ProofRpcModuleTests
{
    private readonly bool _createSystemAccount;
    private readonly bool _useNonZeroGasPrice;
    private IProofRpcModule _proofRpcModule = null!;
    private IBlockTree _blockTree = null!;
    private IDbProvider _dbProvider = null!;
    private TestSpecProvider _specProvider = null!;
    private WorldStateManager _worldStateManager = null!;
    private IReadOnlyTxProcessingEnvFactory _readOnlyTxProcessingEnvFactory = null!;

    public ProofRpcModuleTests(bool createSystemAccount, bool useNonZeroGasPrice)
    {
        _createSystemAccount = createSystemAccount;
        _useNonZeroGasPrice = useNonZeroGasPrice;
    }

    [SetUp]
    public async Task Setup()
    {
        _dbProvider = await TestMemDbProvider.InitAsync();
        _worldStateManager = TestWorldStateFactory.CreateForTest(_dbProvider, LimboLogs.Instance);

        IWorldState worldState = _worldStateManager.GlobalWorldState;
        worldState.CreateAccount(TestItem.AddressA, 100000);
        worldState.Commit(London.Instance);
        worldState.CommitTree(0);

        InMemoryReceiptStorage receiptStorage = new();
        _specProvider = new TestSpecProvider(London.Instance);
        _blockTree = Build.A.BlockTree(new Block(Build.A.BlockHeader.WithStateRoot(worldState.StateRoot).TestObject, new BlockBody()), _specProvider)
            .WithTransactions(receiptStorage)
            .OfChainLength(10)
            .TestObject;

        _readOnlyTxProcessingEnvFactory = new ReadOnlyTxProcessingEnvFactory(_worldStateManager, _blockTree, _specProvider, LimboLogs.Instance);

        ProofModuleFactory moduleFactory = new(
            _worldStateManager,
            _readOnlyTxProcessingEnvFactory,
            _blockTree,
            new CompositeBlockPreprocessorStep(new RecoverSignatures(new EthereumEcdsa(TestBlockchainIds.ChainId), NullTxPool.Instance, _specProvider, LimboLogs.Instance)),
            receiptStorage,
            _specProvider,
            LimboLogs.Instance);

        _proofRpcModule = moduleFactory.Create();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Can_get_transaction(bool withHeader)
    {
        Hash256 txHash = _blockTree.FindBlock(1)!.Transactions[0].Hash!;
        TransactionForRpcWithProof txWithProof = _proofRpcModule.proof_getTransactionByHash(txHash, withHeader).Data;
        Assert.That(txWithProof.Transaction, Is.Not.Null);
        Assert.That(txWithProof.TxProof.Length, Is.EqualTo(2));
        if (withHeader)
        {
            Assert.That(txWithProof.BlockHeader, Is.Not.Null);
        }
        else
        {
            Assert.That(txWithProof.BlockHeader, Is.Null);
        }

        string response = await RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionByHash", txHash, withHeader);
        Assert.That(response.Contains("\"result\""), Is.True);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task When_getting_non_existing_tx_correct_error_code_is_returned(bool withHeader)
    {
        Hash256 txHash = TestItem.KeccakH;
        string response = await RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionByHash", txHash, withHeader);
        Assert.That(response.Contains($"{ErrorCodes.ResourceNotFound}"), Is.True);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task When_getting_non_existing_receipt_correct_error_code_is_returned(bool withHeader)
    {
        Hash256 txHash = TestItem.KeccakH;
        string response = await RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionReceipt", txHash, withHeader);
        Assert.That(response.Contains($"{ErrorCodes.ResourceNotFound}"), Is.True);
    }

    [TestCase]
    public async Task On_incorrect_params_returns_correct_error_code()
    {
        Hash256 txHash = TestItem.KeccakH;

        // missing with header
        string response = await RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionReceipt", txHash);
        Assert.That(response.Contains($"{ErrorCodes.InvalidParams}"), Is.True, "missing");

        // too many
        response = await RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionReceipt", txHash, true, false);
        Assert.That(response.Contains($"{ErrorCodes.InvalidParams}"), Is.True, "too many");

        // missing with header
        response = await RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionByHash", txHash);
        Assert.That(response.Contains($"{ErrorCodes.InvalidParams}"), Is.True, "missing");

        // too many
        response = await RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionByHash", txHash, true, false);
        Assert.That(response.Contains($"{ErrorCodes.InvalidParams}"), Is.True, "too many");

        // all wrong
        response = await RpcTest.TestSerializedRequest(_proofRpcModule, "proof_call", txHash);
        Assert.That(response.Contains($"{ErrorCodes.InvalidParams}"), Is.True, "missing");
    }

    [TestCase(true, "{\"jsonrpc\":\"2.0\",\"result\":{\"receipt\":{\"transactionHash\":\"0x9d335cdd632432bc4181dabfc07b9a614f1fcf9f0d2c0c1340e35a403875fdb1\",\"transactionIndex\":\"0x0\",\"blockHash\":\"0xda4b917515655b1aabcc9b01125df34a76c6ebb3e7e2f2b060d4daa70d9f813d\",\"blockNumber\":\"0x1\",\"cumulativeGasUsed\":\"0x0\",\"gasUsed\":\"0x0\",\"effectiveGasPrice\":\"0x1\",\"to\":null,\"contractAddress\":null,\"logs\":[],\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"status\":\"0x0\",\"type\":\"0x0\"},\"txProof\":[\"0xf851a0eb9c9ef295ba68ff22c85763176dabc05773d58ef77ce34e4a23bf9516c706bc80808080808080a0850e08970f6beee9bd3687c74e591429cf6f65d5faf9db298ddc627ac4a26a1b8080808080808080\",\"0xf86530b862f860800182a41094000000000000000000000000000000000000000001818026a0e4830571029d291f22478cbb60a04115f783fb687f9c3a98bf9d4a008f909817a010f0f7a1c274747616522ea29771cb026bf153362227563e2657d25fa57816bd\"],\"receiptProof\":[\"0xf851a0460919cda4f025e4e91b9540e4a0fb8a2cf07e4ad8b2379a053efe2f98b1789980808080808080a0bc8717240b46db28e32bc834f8c34f4d70c2e9ba880eb68de904351fd5ef158f8080808080808080\",\"0xf9010d30b90109f901060180b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c0\"],\"blockHeader\":\"0xf901f9a0a3e31eb259593976b3717142a5a9e90637f614d33e2ad13f01134ea00c24ca5aa01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a009e11c477e0a0dfdfe036492b9bce7131991eb23bcf9575f9bff1e4016f90447a0e1b1585a222beceb3887dc6701802facccf186c2d0f6aa69e26ae0c431fc2b5db9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000830f424001833d090080830f424183010203a02ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e28800000000000003e8\"},\"id\":67}")]
    [TestCase(false, "{\"jsonrpc\":\"2.0\",\"result\":{\"receipt\":{\"transactionHash\":\"0x9d335cdd632432bc4181dabfc07b9a614f1fcf9f0d2c0c1340e35a403875fdb1\",\"transactionIndex\":\"0x0\",\"blockHash\":\"0xda4b917515655b1aabcc9b01125df34a76c6ebb3e7e2f2b060d4daa70d9f813d\",\"blockNumber\":\"0x1\",\"cumulativeGasUsed\":\"0x0\",\"gasUsed\":\"0x0\",\"effectiveGasPrice\":\"0x1\",\"to\":null,\"contractAddress\":null,\"logs\":[],\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"status\":\"0x0\",\"type\":\"0x0\"},\"txProof\":[\"0xf851a0eb9c9ef295ba68ff22c85763176dabc05773d58ef77ce34e4a23bf9516c706bc80808080808080a0850e08970f6beee9bd3687c74e591429cf6f65d5faf9db298ddc627ac4a26a1b8080808080808080\",\"0xf86530b862f860800182a41094000000000000000000000000000000000000000001818026a0e4830571029d291f22478cbb60a04115f783fb687f9c3a98bf9d4a008f909817a010f0f7a1c274747616522ea29771cb026bf153362227563e2657d25fa57816bd\"],\"receiptProof\":[\"0xf851a0460919cda4f025e4e91b9540e4a0fb8a2cf07e4ad8b2379a053efe2f98b1789980808080808080a0bc8717240b46db28e32bc834f8c34f4d70c2e9ba880eb68de904351fd5ef158f8080808080808080\",\"0xf9010d30b90109f901060180b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c0\"]},\"id\":67}")]
    public async Task Can_get_receipt(bool withHeader, string expectedResult)
    {
        Hash256 txHash = _blockTree.FindBlock(1)!.Transactions[0].Hash!;
        ReceiptWithProof receiptWithProof = _proofRpcModule.proof_getTransactionReceipt(txHash, withHeader).Data;
        Assert.That(receiptWithProof.Receipt, Is.Not.Null);
        Assert.That(receiptWithProof.ReceiptProof.Length, Is.EqualTo(2));

        if (withHeader)
        {
            Assert.That(receiptWithProof.BlockHeader, Is.Not.Null);
        }
        else
        {
            Assert.That(receiptWithProof.BlockHeader, Is.Null);
        }

        string response = await RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionReceipt", txHash, withHeader);
        response.Should().Be(expectedResult);
    }

    [TestCase(true, "{\"jsonrpc\":\"2.0\",\"result\":{\"receipt\":{\"transactionHash\":\"0x4901390ae91e8a4286f7ae9053440c48eb5c2bca11ca83439f0088a4af90ceb8\",\"transactionIndex\":\"0x1\",\"blockHash\":\"0xda4b917515655b1aabcc9b01125df34a76c6ebb3e7e2f2b060d4daa70d9f813d\",\"blockNumber\":\"0x1\",\"cumulativeGasUsed\":\"0x7d0\",\"gasUsed\":\"0x3e8\",\"effectiveGasPrice\":\"0x1\",\"from\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"to\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"contractAddress\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"logs\":[{\"removed\":false,\"logIndex\":\"0x2\",\"transactionIndex\":\"0x1\",\"transactionHash\":\"0x4901390ae91e8a4286f7ae9053440c48eb5c2bca11ca83439f0088a4af90ceb8\",\"blockHash\":\"0xda4b917515655b1aabcc9b01125df34a76c6ebb3e7e2f2b060d4daa70d9f813d\",\"blockNumber\":\"0x1\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]},{\"removed\":false,\"logIndex\":\"0x3\",\"transactionIndex\":\"0x1\",\"transactionHash\":\"0x4901390ae91e8a4286f7ae9053440c48eb5c2bca11ca83439f0088a4af90ceb8\",\"blockHash\":\"0xda4b917515655b1aabcc9b01125df34a76c6ebb3e7e2f2b060d4daa70d9f813d\",\"blockNumber\":\"0x1\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]}],\"logsBloom\":\"0x00000000000000000080000000000000000000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000800000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000000000\",\"status\":\"0x0\",\"type\":\"0x0\"},\"txProof\":[\"0xf851a0eb9c9ef295ba68ff22c85763176dabc05773d58ef77ce34e4a23bf9516c706bc80808080808080a0850e08970f6beee9bd3687c74e591429cf6f65d5faf9db298ddc627ac4a26a1b8080808080808080\",\"0xf86431b861f85f010182a410940000000000000000000000000000000000000000020126a0872929cb57ab6d88d0004a60f00df3dd9e0755860549aea25e559bce3d4a66dba01c06266ee2085ae815c258dd9dbb601bfc08c35c13b7cc9cd4ed88a16c3eb3f0\"],\"receiptProof\":[\"0xf851a0460919cda4f025e4e91b9540e4a0fb8a2cf07e4ad8b2379a053efe2f98b1789980808080808080a0bc8717240b46db28e32bc834f8c34f4d70c2e9ba880eb68de904351fd5ef158f8080808080808080\",\"0xf9010d31b90109f901060180b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c0\"],\"blockHeader\":\"0xf901f9a0a3e31eb259593976b3717142a5a9e90637f614d33e2ad13f01134ea00c24ca5aa01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a009e11c477e0a0dfdfe036492b9bce7131991eb23bcf9575f9bff1e4016f90447a0e1b1585a222beceb3887dc6701802facccf186c2d0f6aa69e26ae0c431fc2b5db9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000830f424001833d090080830f424183010203a02ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e28800000000000003e8\"},\"id\":67}")]
    [TestCase(false, "{\"jsonrpc\":\"2.0\",\"result\":{\"receipt\":{\"transactionHash\":\"0x4901390ae91e8a4286f7ae9053440c48eb5c2bca11ca83439f0088a4af90ceb8\",\"transactionIndex\":\"0x1\",\"blockHash\":\"0xda4b917515655b1aabcc9b01125df34a76c6ebb3e7e2f2b060d4daa70d9f813d\",\"blockNumber\":\"0x1\",\"cumulativeGasUsed\":\"0x7d0\",\"gasUsed\":\"0x3e8\",\"effectiveGasPrice\":\"0x1\",\"from\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"to\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"contractAddress\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"logs\":[{\"removed\":false,\"logIndex\":\"0x2\",\"transactionIndex\":\"0x1\",\"transactionHash\":\"0x4901390ae91e8a4286f7ae9053440c48eb5c2bca11ca83439f0088a4af90ceb8\",\"blockHash\":\"0xda4b917515655b1aabcc9b01125df34a76c6ebb3e7e2f2b060d4daa70d9f813d\",\"blockNumber\":\"0x1\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]},{\"removed\":false,\"logIndex\":\"0x3\",\"transactionIndex\":\"0x1\",\"transactionHash\":\"0x4901390ae91e8a4286f7ae9053440c48eb5c2bca11ca83439f0088a4af90ceb8\",\"blockHash\":\"0xda4b917515655b1aabcc9b01125df34a76c6ebb3e7e2f2b060d4daa70d9f813d\",\"blockNumber\":\"0x1\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]}],\"logsBloom\":\"0x00000000000000000080000000000000000000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000800000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000000000\",\"status\":\"0x0\",\"type\":\"0x0\"},\"txProof\":[\"0xf851a0eb9c9ef295ba68ff22c85763176dabc05773d58ef77ce34e4a23bf9516c706bc80808080808080a0850e08970f6beee9bd3687c74e591429cf6f65d5faf9db298ddc627ac4a26a1b8080808080808080\",\"0xf86431b861f85f010182a410940000000000000000000000000000000000000000020126a0872929cb57ab6d88d0004a60f00df3dd9e0755860549aea25e559bce3d4a66dba01c06266ee2085ae815c258dd9dbb601bfc08c35c13b7cc9cd4ed88a16c3eb3f0\"],\"receiptProof\":[\"0xf851a0460919cda4f025e4e91b9540e4a0fb8a2cf07e4ad8b2379a053efe2f98b1789980808080808080a0bc8717240b46db28e32bc834f8c34f4d70c2e9ba880eb68de904351fd5ef158f8080808080808080\",\"0xf9010d31b90109f901060180b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c0\"]},\"id\":67}")]
    public async Task Get_receipt_when_block_has_few_receipts(bool withHeader, string expectedResult)
    {
        IReceiptFinder _receiptFinder = Substitute.For<IReceiptFinder>();
        LogEntry[] logEntries = new[] { Build.A.LogEntry.TestObject, Build.A.LogEntry.TestObject };

        TxReceipt receipt1 = new TxReceipt()
        {
            Bloom = new Bloom(logEntries),
            Index = 0,
            Recipient = TestItem.AddressA,
            Sender = TestItem.AddressB,
            BlockHash = _blockTree.FindBlock(1)!.Hash,
            BlockNumber = 1,
            ContractAddress = TestItem.AddressC,
            GasUsed = 1000,
            TxHash = _blockTree.FindBlock(1)!.Transactions[0].Hash,
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
            BlockHash = _blockTree.FindBlock(1)!.Hash,
            BlockNumber = 1,
            ContractAddress = TestItem.AddressC,
            GasUsed = 1000,
            TxHash = _blockTree.FindBlock(1)!.Transactions[1].Hash,
            StatusCode = 0,
            GasUsedTotal = 2000,
            Logs = logEntries
        };
        _ = _blockTree.FindBlock(1)!;
        Hash256 txHash = _blockTree.FindBlock(1)!.Transactions[1].Hash!;
        TxReceipt[] receipts = { receipt1, receipt2 };
        _receiptFinder.Get(Arg.Any<Block>()).Returns(receipts);
        _receiptFinder.Get(Arg.Any<Hash256>()).Returns(receipts);
        _receiptFinder.FindBlockHash(Arg.Any<Hash256>()).Returns(_blockTree.FindBlock(1)!.Hash);

        ProofModuleFactory moduleFactory = new ProofModuleFactory(
            _worldStateManager,
            _readOnlyTxProcessingEnvFactory,
            _blockTree,
            new CompositeBlockPreprocessorStep(new RecoverSignatures(new EthereumEcdsa(TestBlockchainIds.ChainId), NullTxPool.Instance, _specProvider, LimboLogs.Instance)),
            _receiptFinder,
            _specProvider,
            LimboLogs.Instance);

        _proofRpcModule = moduleFactory.Create();
        ReceiptWithProof receiptWithProof = _proofRpcModule.proof_getTransactionReceipt(txHash, withHeader).Data;

        if (withHeader)
        {
            Assert.That(receiptWithProof.BlockHeader, Is.Not.Null);
        }
        else
        {
            Assert.That(receiptWithProof.BlockHeader, Is.Null);
        }

        string response = await RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionReceipt", txHash, withHeader);
        response.Should().Be(expectedResult);
    }

    [TestCase]
    public async Task Can_call()
    {
        WorldState stateProvider = CreateInitialState(null);

        Hash256 root = stateProvider.StateRoot;
        Block block = Build.A.Block.WithParent(_blockTree.Head!).WithStateRoot(root).TestObject;
        BlockTreeBuilder.AddBlock(_blockTree, block);

        // would need to setup state root somehow...

        TransactionForRpc tx = new LegacyTransactionForRpc
        {
            From = TestItem.AddressA,
            To = TestItem.AddressB,
            GasPrice = _useNonZeroGasPrice ? 10.GWei() : 0
        };

        _proofRpcModule.proof_call(tx, new BlockParameter(block.Number));

        string response = await RpcTest.TestSerializedRequest(_proofRpcModule, "proof_call", tx, block.Number);
        Assert.That(response.Contains("\"result\""), Is.True);
    }

    [TestCase]
    public async Task Can_call_by_hash()
    {
        WorldState stateProvider = CreateInitialState(null);

        Hash256 root = stateProvider.StateRoot;
        Block block = Build.A.Block.WithParent(_blockTree.Head!).WithStateRoot(root).TestObject;
        BlockTreeBuilder.AddBlock(_blockTree, block);

        // would need to setup state root somehow...

        TransactionForRpc tx = new LegacyTransactionForRpc
        {
            From = TestItem.AddressA,
            To = TestItem.AddressB,
            GasPrice = _useNonZeroGasPrice ? 10.GWei() : 0
        };
        _proofRpcModule.proof_call(tx, new BlockParameter(block.Hash!));

        string response = await RpcTest.TestSerializedRequest(_proofRpcModule, "proof_call", tx, block.Hash);
        Assert.That(response.Contains("\"result\""), Is.True);
    }

    [TestCase]
    public async Task Can_call_by_hash_canonical()
    {
        Block lastHead = _blockTree.Head!;
        Block block = Build.A.Block.WithParent(lastHead).TestObject;
        Block newBlockOnMain = Build.A.Block.WithParent(lastHead).WithDifficulty(block.Difficulty + 1).TestObject;
        BlockTreeBuilder.AddBlock(_blockTree, block);
        BlockTreeBuilder.AddBlock(_blockTree, newBlockOnMain);

        // would need to setup state root somehow...

        TransactionForRpc tx = new LegacyTransactionForRpc
        {
            From = TestItem.AddressA,
            To = TestItem.AddressB,
            GasPrice = _useNonZeroGasPrice ? 10.GWei() : 0
        };

        string response = await RpcTest.TestSerializedRequest(_proofRpcModule, "proof_call", tx, new { blockHash = block.Hash, requireCanonical = true });
        Assert.That(response.Contains("-32000"), Is.True);

        response = await RpcTest.TestSerializedRequest(_proofRpcModule, "proof_call", tx, new { blockHash = TestItem.KeccakG, requireCanonical = true });
        Assert.That(response.Contains("-32001"), Is.True);
    }

    [TestCase]
    public async Task Can_call_with_block_hashes()
    {
        byte[] code = Prepare.EvmCode
            .PushData("0x01")
            .Op(Instruction.BLOCKHASH)
            .Done;
        CallResultWithProof result = await TestCallWithCode(code);
        Assert.That(result.BlockHeaders.Length, Is.EqualTo(2));
    }

    [TestCase]
    public async Task Can_call_with_many_block_hashes()
    {
        byte[] code = Prepare.EvmCode
            .PushData("0x01")
            .Op(Instruction.BLOCKHASH)
            .PushData("0x02")
            .Op(Instruction.BLOCKHASH)
            .Done;
        CallResultWithProof result = await TestCallWithCode(code);
        Assert.That(result.BlockHeaders.Length, Is.EqualTo(3));
    }

    [TestCase]
    public async Task Can_call_with_same_block_hash_many_time()
    {
        byte[] code = Prepare.EvmCode
            .PushData("0x01")
            .Op(Instruction.BLOCKHASH)
            .PushData("0x01")
            .Op(Instruction.BLOCKHASH)
            .Done;
        CallResultWithProof result = await TestCallWithCode(code);
        Assert.That(result.BlockHeaders.Length, Is.EqualTo(2));
    }

    [TestCase]
    public async Task Can_call_with_storage_load()
    {
        byte[] code = Prepare.EvmCode
            .PushData("0x01")
            .Op(Instruction.SLOAD)
            .Done;

        CallResultWithProof result = await TestCallWithCode(code);
        Assert.That(result.Accounts.Length, Is.EqualTo(1 + (_useNonZeroGasPrice ? 1 : 0)));
    }

    [TestCase]
    public async Task Can_call_with_many_storage_loads()
    {
        byte[] code = Prepare.EvmCode
            .PushData("0x01")
            .Op(Instruction.SLOAD)
            .PushData("0x02")
            .Op(Instruction.SLOAD)
            .Done;
        CallResultWithProof result = await TestCallWithCode(code);
        Assert.That(result.Accounts.Length, Is.EqualTo(1 + (_useNonZeroGasPrice ? 1 : 0)));
    }

    [TestCase]
    public async Task Can_call_with_storage_write()
    {
        byte[] code = Prepare.EvmCode
            .PushData("0x01")
            .PushData("0x01")
            .Op(Instruction.SSTORE)
            .Done;

        CallResultWithProof result = await TestCallWithCode(code);
        Assert.That(result.Accounts.Length, Is.EqualTo(1 + (_useNonZeroGasPrice ? 1 : 0)));
    }

    [TestCase]
    public async Task Can_call_with_extcodecopy()
    {
        byte[] code = Prepare.EvmCode
            .PushData("0x20")
            .PushData("0x00")
            .PushData("0x00")
            .PushData(TestItem.AddressC)
            .Op(Instruction.EXTCODECOPY)
            .Done;
        CallResultWithProof result = await TestCallWithCode(code);
        Assert.That(result.Accounts.Length, Is.EqualTo(2 + (_useNonZeroGasPrice ? 1 : 0)));
    }

    [TestCase]
    public async Task Can_call_with_extcodecopy_to_system_account()
    {
        byte[] code = Prepare.EvmCode
            .PushData("0x20")
            .PushData("0x00")
            .PushData("0x00")
            .PushData(Address.SystemUser)
            .Op(Instruction.EXTCODECOPY)
            .Done;
        CallResultWithProof result = await TestCallWithCode(code);
        Assert.That(result.Accounts.Length, Is.EqualTo(2));
    }

    [TestCase]
    public async Task Can_call_with_extcodesize()
    {
        byte[] code = Prepare.EvmCode
            .PushData(TestItem.AddressC)
            .Op(Instruction.EXTCODESIZE)
            .Done;
        CallResultWithProof result = await TestCallWithCode(code);
        Assert.That(result.Accounts.Length, Is.EqualTo(2 + (_useNonZeroGasPrice ? 1 : 0)));
    }

    [TestCase]
    public async Task Can_call_with_extcodesize_to_system_account()
    {
        byte[] code = Prepare.EvmCode
            .PushData(Address.SystemUser)
            .Op(Instruction.EXTCODESIZE)
            .Done;
        CallResultWithProof result = await TestCallWithCode(code);
        Assert.That(result.Accounts.Length, Is.EqualTo(2));
    }

    [TestCase]
    public async Task Can_call_with_extcodehash()
    {
        _specProvider.SpecToReturn = MuirGlacier.Instance;
        byte[] code = Prepare.EvmCode
            .PushData(TestItem.AddressC)
            .Op(Instruction.EXTCODEHASH)
            .Done;
        CallResultWithProof result = await TestCallWithCode(code);
        Assert.That(result.Accounts.Length, Is.EqualTo(2 + (_useNonZeroGasPrice ? 1 : 0)));
    }

    [TestCase]
    public async Task Can_call_with_extcodehash_to_system_account()
    {
        _specProvider.SpecToReturn = MuirGlacier.Instance;
        byte[] code = Prepare.EvmCode
            .PushData(Address.SystemUser)
            .Op(Instruction.EXTCODEHASH)
            .Done;
        CallResultWithProof result = await TestCallWithCode(code);
        Assert.That(result.Accounts.Length, Is.EqualTo(2));
    }

    [TestCase]
    public async Task Can_call_with_just_basic_addresses()
    {
        _specProvider.SpecToReturn = MuirGlacier.Instance;
        byte[] code = Prepare.EvmCode
            .Op(Instruction.STOP)
            .Done;
        CallResultWithProof result = await TestCallWithCode(code);
        Assert.That(result.Accounts.Length, Is.EqualTo(1 + (_useNonZeroGasPrice ? 1 : 0)));
    }

    [TestCase]
    public async Task Can_call_with_balance()
    {
        _specProvider.SpecToReturn = MuirGlacier.Instance;
        byte[] code = Prepare.EvmCode
            .PushData(TestItem.AddressC)
            .Op(Instruction.BALANCE)
            .Done;

        CallResultWithProof result = await TestCallWithCode(code);
        Assert.That(result.Accounts.Length, Is.EqualTo(2 + (_useNonZeroGasPrice ? 1 : 0)));
    }

    [TestCase]
    public async Task Can_call_with_self_balance()
    {
        _specProvider.SpecToReturn = MuirGlacier.Instance;
        byte[] code = Prepare.EvmCode
            .Op(Instruction.SELFBALANCE)
            .Done;

        CallResultWithProof result = await TestCallWithCode(code);
        Assert.That(result.Accounts.Length, Is.EqualTo(1 + (_useNonZeroGasPrice ? 1 : 0)));
    }

    [TestCase]
    public async Task Can_call_with_balance_of_system_account()
    {
        _specProvider.SpecToReturn = MuirGlacier.Instance;
        byte[] code = Prepare.EvmCode
            .PushData(Address.SystemUser)
            .Op(Instruction.BALANCE)
            .Done;
        CallResultWithProof result = await TestCallWithCode(code);
        Assert.That(result.Accounts.Length, Is.EqualTo(2));
    }

    [TestCase]
    public async Task Can_call_with_call_to_system_account_with_zero_value()
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
        CallResultWithProof result = await TestCallWithCode(code);
        Assert.That(result.Accounts.Length, Is.EqualTo(2));
    }

    [TestCase]
    public async Task Can_call_with_static_call_to_system_account()
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
        CallResultWithProof result = await TestCallWithCode(code);
        Assert.That(result.Accounts.Length, Is.EqualTo(2));
    }

    [TestCase]
    public async Task Can_call_with_delegate_call_to_system_account()
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
        CallResultWithProof result = await TestCallWithCode(code);
        Assert.That(result.Accounts.Length, Is.EqualTo(2));
    }

    [TestCase]
    public async Task Can_call_with_call_to_system_account_with_non_zero_value()
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
        CallResultWithProof result = await TestCallWithCode(code);
        Assert.That(result.Accounts.Length, Is.EqualTo(2));
    }

    [TestCase]
    public async Task Can_call_with_call_with_zero_value()
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
        CallResultWithProof result = await TestCallWithCode(code);
        Assert.That(result.Accounts.Length, Is.EqualTo(2 + (_useNonZeroGasPrice ? 1 : 0)));
    }

    [TestCase]
    public async Task Can_call_with_static_call()
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
        CallResultWithProof result = await TestCallWithCode(code);
        Assert.That(result.Accounts.Length, Is.EqualTo(2 + (_useNonZeroGasPrice ? 1 : 0)));
    }

    [TestCase]
    public async Task Can_call_with_delegate_call()
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
        CallResultWithProof result = await TestCallWithCode(code);
        Assert.That(result.Accounts.Length, Is.EqualTo(_createSystemAccount && _useNonZeroGasPrice ? 3 : 2));
    }

    [TestCase]
    public async Task Can_call_with_call_with_non_zero_value()
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
        CallResultWithProof result = await TestCallWithCode(code);
        Assert.That(result.Accounts.Length, Is.EqualTo(2 + (_useNonZeroGasPrice ? 1 : 0)));
    }

    [TestCase]
    public async Task Can_call_with_self_destruct()
    {
        _specProvider.SpecToReturn = MuirGlacier.Instance;
        byte[] code = Prepare.EvmCode
            .PushData(TestItem.AddressC)
            .Op(Instruction.SELFDESTRUCT)
            .Done;
        CallResultWithProof result = await TestCallWithCode(code);

        Assert.That(result.Accounts.Length, Is.EqualTo(2 + (_useNonZeroGasPrice ? 1 : 0)));
    }

    [TestCase]
    public async Task Can_call_with_self_destruct_to_system_account()
    {
        _specProvider.SpecToReturn = MuirGlacier.Instance;
        byte[] code = Prepare.EvmCode
            .PushData(Address.SystemUser)
            .Op(Instruction.SELFDESTRUCT)
            .Done;
        CallResultWithProof result = await TestCallWithCode(code);
        Assert.That(result.Accounts.Length, Is.EqualTo(2));
    }


    [TestCase]
    public async Task Can_call_with_many_storage_writes()
    {
        byte[] code = Prepare.EvmCode
            .PushData("0x01")
            .PushData("0x01")
            .Op(Instruction.SSTORE)
            .PushData("0x02")
            .PushData("0x02")
            .Op(Instruction.SSTORE)
            .Done;
        CallResultWithProof result = await TestCallWithCode(code);
        Assert.That(result.Accounts.Length, Is.EqualTo(1 + (_useNonZeroGasPrice ? 1 : 0)));
    }

    [TestCase]
    public async Task Can_call_with_mix_of_everything()
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

        await TestCallWithCode(code);
    }

    [TestCase]
    public async Task Can_call_with_mix_of_everything_and_storage()
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

        await TestCallWithStorageAndCode(code, _useNonZeroGasPrice ? 10.GWei() : 0);
    }

    [TestCase]
    public async Task Can_call_with_mix_of_everything_and_storage_from_another_account_wrong_nonce()
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

        await TestCallWithStorageAndCode(code, 0, TestItem.AddressD);
    }

    private async Task<CallResultWithProof> TestCallWithCode(byte[] code, Address? from = null)
    {
        WorldState stateProvider = CreateInitialState(code);

        Hash256 root = stateProvider.StateRoot;
        Block block = Build.A.Block.WithParent(_blockTree.Head!).WithStateRoot(root).WithBeneficiary(TestItem.AddressD).TestObject;
        BlockTreeBuilder.AddBlock(_blockTree, block);
        Block blockOnTop = Build.A.Block.WithParent(block).WithStateRoot(root).WithBeneficiary(TestItem.AddressD).TestObject;
        BlockTreeBuilder.AddBlock(_blockTree, blockOnTop);

        // would need to setup state root somehow...

        TransactionForRpc tx = new LegacyTransactionForRpc
        {
            From = from,
            To = TestItem.AddressB,
            GasPrice = _useNonZeroGasPrice ? 10.GWei() : 0
        };

        CallResultWithProof callResultWithProof = _proofRpcModule.proof_call(tx, new BlockParameter(blockOnTop.Number)).Data;
        Assert.That(callResultWithProof.Accounts.Length, Is.GreaterThan(0));

        foreach (AccountProof accountProof in callResultWithProof.Accounts)
        {
            ProofVerifier.VerifyOneProof(accountProof.Proof!, block.StateRoot!);
            foreach (StorageProof storageProof in accountProof.StorageProofs!)
            {
                ProofVerifier.VerifyOneProof(storageProof.Proof!, accountProof.StorageRoot);
            }
        }

        string response = await RpcTest.TestSerializedRequest(_proofRpcModule, "proof_call", tx, blockOnTop.Number);
        Assert.That(response.Contains("\"result\""), Is.True);

        return callResultWithProof;
    }

    private async Task TestCallWithStorageAndCode(byte[] code, UInt256 gasPrice, Address? from = null)
    {
        WorldState stateProvider = CreateInitialState(code);

        for (int i = 0; i < 10000; i++)
        {
            stateProvider.Set(new StorageCell(TestItem.AddressB, (UInt256)i), i.ToBigEndianByteArray());
        }

        stateProvider.Commit(MainnetSpecProvider.Instance.GenesisSpec, NullStateTracer.Instance);
        stateProvider.CommitTree(0);

        Hash256 root = stateProvider.StateRoot;

        Block block = Build.A.Block.WithParent(_blockTree.Head!).WithStateRoot(root).TestObject;
        BlockTreeBuilder.AddBlock(_blockTree, block);
        Block blockOnTop = Build.A.Block.WithParent(block).WithStateRoot(root).TestObject;
        BlockTreeBuilder.AddBlock(_blockTree, blockOnTop);

        // would need to setup state root somehow...

        TransactionForRpc tx = new LegacyTransactionForRpc
        {
            // we are testing system transaction here when From is null
            From = from,
            To = TestItem.AddressB,
            GasPrice = gasPrice,
            Nonce = 1000
        };

        CallResultWithProof callResultWithProof = _proofRpcModule.proof_call(tx, new BlockParameter(blockOnTop.Number)).Data;
        Assert.That(callResultWithProof.Accounts.Length, Is.GreaterThan(0));

        // just the keys for debugging
        byte[] span = new byte[32];
        new UInt256(0).ToBigEndian(span);
        _ = Keccak.Compute(span);

        // just the keys for debugging
        new UInt256(1).ToBigEndian(span);
        _ = Keccak.Compute(span);

        // just the keys for debugging
        new UInt256(2).ToBigEndian(span);
        _ = Keccak.Compute(span);

        foreach (AccountProof accountProof in callResultWithProof.Accounts)
        {
            // this is here for diagnostics - so you can read what happens in the test
            // generally the account here should be consistent with the values inside the proof
            // the exception will be thrown if the account did not exist before the call
            try
            {
                CappedArray<byte> verifyOneProof = ProofVerifier.VerifyOneProof(accountProof.Proof!, block.StateRoot!);
                new AccountDecoder().Decode(verifyOneProof.AsSpan());
            }
            catch (Exception)
            {
                // ignored
            }

            foreach (StorageProof storageProof in accountProof.StorageProofs!)
            {
                // we read the values here just to allow easier debugging so you can confirm that the value is same as the one in the proof and in the trie
                ProofVerifier.VerifyOneProof(storageProof.Proof!, accountProof.StorageRoot);
            }
        }

        string response = await RpcTest.TestSerializedRequest(_proofRpcModule, "proof_call", tx, blockOnTop.Number);
        Assert.That(response.Contains("\"result\""), Is.True);
    }

    private WorldState CreateInitialState(byte[]? code)
    {
        WorldState stateProvider = new(TestTrieStoreFactory.Build(_dbProvider.StateDb, LimboLogs.Instance), _dbProvider.CodeDb, LimboLogs.Instance);
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

    private void AddAccount(WorldState stateProvider, Address account, UInt256 initialBalance)
    {
        stateProvider.CreateAccount(account, initialBalance);
        stateProvider.Commit(MuirGlacier.Instance, NullStateTracer.Instance);
    }

    private void AddCode(WorldState stateProvider, Address account, byte[] code)
    {
        stateProvider.InsertCode(account, code, MuirGlacier.Instance);
        stateProvider.Commit(MainnetSpecProvider.Instance.GenesisSpec, NullStateTracer.Instance);
    }
}
