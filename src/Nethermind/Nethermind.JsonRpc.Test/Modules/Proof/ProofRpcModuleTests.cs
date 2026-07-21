// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.JsonRpc.Modules.Proof;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Evm.State;
using NUnit.Framework;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Headers;
using Nethermind.Config;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Db;
using Nethermind.Core.Test.Modules;
using Nethermind.JsonRpc.Modules;
using Nethermind.State;
using NSubstitute;

namespace Nethermind.JsonRpc.Test.Modules.Proof;

[Parallelizable(ParallelScope.None)]
public class ProofRpcModuleTests
{
    private IProofRpcModule _proofRpcModule = null!;
    private IBlockTree _blockTree = null!;
    private IDbProvider _dbProvider = null!;
    private TestSpecProvider _specProvider = null!;
    private WorldStateManager _worldStateManager = null!;
    private IContainer _container;

    [SetUp]
    public async Task Setup()
    {
        _dbProvider = await TestMemDbProvider.InitAsync();
        _worldStateManager = TestWorldStateFactory.CreateWorldStateManagerForTest(_dbProvider, LimboLogs.Instance);

        Hash256 stateRoot;
        IWorldState worldState = new WorldState(_worldStateManager.GlobalWorldState, LimboLogs.Instance);
        using (IDisposable _ = worldState.BeginScope(IWorldState.PreGenesis))
        {
            worldState.CreateAccount(TestItem.AddressA, 100000);
            worldState.Commit(London.Instance);
            worldState.CommitTree(0);
            stateRoot = worldState.StateRoot;
        }

        InMemoryReceiptStorage receiptStorage = new();
        _specProvider = new TestSpecProvider(London.Instance);
        BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree(new Block(Build.A.BlockHeader.WithStateRoot(stateRoot).TestObject, new BlockBody()), _specProvider)
            .WithTransactions(receiptStorage)
            .OfChainLength(10);
        _blockTree = blockTreeBuilder.TestObject;

        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new ConfigProvider()))
            .AddSingleton<ISpecProvider>(_specProvider)
            .AddSingleton<IBlockTree>(_blockTree)
            .AddSingleton<IDbProvider>(_dbProvider)
            .AddSingleton<IHeaderFinder>(blockTreeBuilder.HeaderStore)
            .AddSingleton<IReceiptStorage>(receiptStorage)
            .AddSingleton<IWorldStateManager>(_worldStateManager)
            .Build();
        _proofRpcModule = _container.Resolve<IRpcModuleFactory<IProofRpcModule>>().Create();
    }

    [TearDown]
    public void TearDown() => _container.Dispose();

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

    [TestCase(true, "{\"jsonrpc\":\"2.0\",\"result\":{\"receipt\":{\"transactionHash\":\"0x9d335cdd632432bc4181dabfc07b9a614f1fcf9f0d2c0c1340e35a403875fdb1\",\"transactionIndex\":\"0x0\",\"blockHash\":\"0xda4b917515655b1aabcc9b01125df34a76c6ebb3e7e2f2b060d4daa70d9f813d\",\"blockNumber\":\"0x1\",\"cumulativeGasUsed\":\"0x0\",\"gasUsed\":\"0x0\",\"effectiveGasPrice\":\"0x1\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x0000000000000000000000000000000000000000\",\"contractAddress\":null,\"logs\":[],\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"status\":\"0x0\",\"type\":\"0x0\"},\"txProof\":[\"0xf851a0eb9c9ef295ba68ff22c85763176dabc05773d58ef77ce34e4a23bf9516c706bc80808080808080a0850e08970f6beee9bd3687c74e591429cf6f65d5faf9db298ddc627ac4a26a1b8080808080808080\",\"0xf86530b862f860800182a41094000000000000000000000000000000000000000001818026a0e4830571029d291f22478cbb60a04115f783fb687f9c3a98bf9d4a008f909817a010f0f7a1c274747616522ea29771cb026bf153362227563e2657d25fa57816bd\"],\"receiptProof\":[\"0xf851a0970464c5f98c507970da7d4e5fc0600a9927d9563cf08ed113dc42772e3ff11080808080808080a07e2d58ec5d664555812c48866e548a5e2e5d51dd5b0540d0c59b6c08932e80818080808080808080\",\"0xf9010f30b9010bf9010801825218b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c0\"],\"blockHeader\":\"0xf901f9a0a3e31eb259593976b3717142a5a9e90637f614d33e2ad13f01134ea00c24ca5aa01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a009e11c477e0a0dfdfe036492b9bce7131991eb23bcf9575f9bff1e4016f90447a0e1b1585a222beceb3887dc6701802facccf186c2d0f6aa69e26ae0c431fc2b5db9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000830f424001833d090080830f424183010203a02ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e28800000000000003e8\"},\"id\":67}")]
    [TestCase(false, "{\"jsonrpc\":\"2.0\",\"result\":{\"receipt\":{\"transactionHash\":\"0x9d335cdd632432bc4181dabfc07b9a614f1fcf9f0d2c0c1340e35a403875fdb1\",\"transactionIndex\":\"0x0\",\"blockHash\":\"0xda4b917515655b1aabcc9b01125df34a76c6ebb3e7e2f2b060d4daa70d9f813d\",\"blockNumber\":\"0x1\",\"cumulativeGasUsed\":\"0x0\",\"gasUsed\":\"0x0\",\"effectiveGasPrice\":\"0x1\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x0000000000000000000000000000000000000000\",\"contractAddress\":null,\"logs\":[],\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"status\":\"0x0\",\"type\":\"0x0\"},\"txProof\":[\"0xf851a0eb9c9ef295ba68ff22c85763176dabc05773d58ef77ce34e4a23bf9516c706bc80808080808080a0850e08970f6beee9bd3687c74e591429cf6f65d5faf9db298ddc627ac4a26a1b8080808080808080\",\"0xf86530b862f860800182a41094000000000000000000000000000000000000000001818026a0e4830571029d291f22478cbb60a04115f783fb687f9c3a98bf9d4a008f909817a010f0f7a1c274747616522ea29771cb026bf153362227563e2657d25fa57816bd\"],\"receiptProof\":[\"0xf851a0970464c5f98c507970da7d4e5fc0600a9927d9563cf08ed113dc42772e3ff11080808080808080a07e2d58ec5d664555812c48866e548a5e2e5d51dd5b0540d0c59b6c08932e80818080808080808080\",\"0xf9010f30b9010bf9010801825218b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c0\"]},\"id\":67}")]
    public async Task Can_get_receipt(bool withHeader, string expectedResult)
    {
        Hash256 txHash = _blockTree.FindBlock(1)!.Transactions[0].Hash!;
        ReceiptWithProof receiptWithProof = _proofRpcModule.proof_getTransactionReceipt(txHash, withHeader).Data;
        Assert.That(receiptWithProof.Receipt, Is.Not.Null);
        Assert.That(receiptWithProof.ReceiptProof.Length, Is.EqualTo(2));

        Assert.That(receiptWithProof.BlockHeader, withHeader ? Is.Not.Null : Is.Null);

        string response = await RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionReceipt", txHash, withHeader);
        Assert.That(response, Is.EqualTo(expectedResult));
    }

    [TestCase(true, "{\"jsonrpc\":\"2.0\",\"result\":{\"receipt\":{\"transactionHash\":\"0x4901390ae91e8a4286f7ae9053440c48eb5c2bca11ca83439f0088a4af90ceb8\",\"transactionIndex\":\"0x1\",\"blockHash\":\"0xda4b917515655b1aabcc9b01125df34a76c6ebb3e7e2f2b060d4daa70d9f813d\",\"blockNumber\":\"0x1\",\"cumulativeGasUsed\":\"0x7d0\",\"gasUsed\":\"0x3e8\",\"effectiveGasPrice\":\"0x1\",\"from\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"to\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"contractAddress\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"logs\":[{\"removed\":false,\"logIndex\":\"0x2\",\"transactionIndex\":\"0x1\",\"transactionHash\":\"0x4901390ae91e8a4286f7ae9053440c48eb5c2bca11ca83439f0088a4af90ceb8\",\"blockHash\":\"0xda4b917515655b1aabcc9b01125df34a76c6ebb3e7e2f2b060d4daa70d9f813d\",\"blockNumber\":\"0x1\",\"blockTimestamp\":\"0xf4241\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]},{\"removed\":false,\"logIndex\":\"0x3\",\"transactionIndex\":\"0x1\",\"transactionHash\":\"0x4901390ae91e8a4286f7ae9053440c48eb5c2bca11ca83439f0088a4af90ceb8\",\"blockHash\":\"0xda4b917515655b1aabcc9b01125df34a76c6ebb3e7e2f2b060d4daa70d9f813d\",\"blockNumber\":\"0x1\",\"blockTimestamp\":\"0xf4241\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]}],\"logsBloom\":\"0x00000000000000000080000000000000000000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000800000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000000000\",\"status\":\"0x0\",\"type\":\"0x0\"},\"txProof\":[\"0xf851a0eb9c9ef295ba68ff22c85763176dabc05773d58ef77ce34e4a23bf9516c706bc80808080808080a0850e08970f6beee9bd3687c74e591429cf6f65d5faf9db298ddc627ac4a26a1b8080808080808080\",\"0xf86431b861f85f010182a410940000000000000000000000000000000000000000020126a0872929cb57ab6d88d0004a60f00df3dd9e0755860549aea25e559bce3d4a66dba01c06266ee2085ae815c258dd9dbb601bfc08c35c13b7cc9cd4ed88a16c3eb3f0\"],\"receiptProof\":[\"0xf851a0970464c5f98c507970da7d4e5fc0600a9927d9563cf08ed113dc42772e3ff11080808080808080a07e2d58ec5d664555812c48866e548a5e2e5d51dd5b0540d0c59b6c08932e80818080808080808080\",\"0xf9010f31b9010bf901080182a430b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c0\"],\"blockHeader\":\"0xf901f9a0a3e31eb259593976b3717142a5a9e90637f614d33e2ad13f01134ea00c24ca5aa01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a009e11c477e0a0dfdfe036492b9bce7131991eb23bcf9575f9bff1e4016f90447a0e1b1585a222beceb3887dc6701802facccf186c2d0f6aa69e26ae0c431fc2b5db9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000830f424001833d090080830f424183010203a02ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e28800000000000003e8\"},\"id\":67}")]
    [TestCase(false, "{\"jsonrpc\":\"2.0\",\"result\":{\"receipt\":{\"transactionHash\":\"0x4901390ae91e8a4286f7ae9053440c48eb5c2bca11ca83439f0088a4af90ceb8\",\"transactionIndex\":\"0x1\",\"blockHash\":\"0xda4b917515655b1aabcc9b01125df34a76c6ebb3e7e2f2b060d4daa70d9f813d\",\"blockNumber\":\"0x1\",\"cumulativeGasUsed\":\"0x7d0\",\"gasUsed\":\"0x3e8\",\"effectiveGasPrice\":\"0x1\",\"from\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"to\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"contractAddress\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"logs\":[{\"removed\":false,\"logIndex\":\"0x2\",\"transactionIndex\":\"0x1\",\"transactionHash\":\"0x4901390ae91e8a4286f7ae9053440c48eb5c2bca11ca83439f0088a4af90ceb8\",\"blockHash\":\"0xda4b917515655b1aabcc9b01125df34a76c6ebb3e7e2f2b060d4daa70d9f813d\",\"blockNumber\":\"0x1\",\"blockTimestamp\":\"0xf4241\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]},{\"removed\":false,\"logIndex\":\"0x3\",\"transactionIndex\":\"0x1\",\"transactionHash\":\"0x4901390ae91e8a4286f7ae9053440c48eb5c2bca11ca83439f0088a4af90ceb8\",\"blockHash\":\"0xda4b917515655b1aabcc9b01125df34a76c6ebb3e7e2f2b060d4daa70d9f813d\",\"blockNumber\":\"0x1\",\"blockTimestamp\":\"0xf4241\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]}],\"logsBloom\":\"0x00000000000000000080000000000000000000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000800000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000000000\",\"status\":\"0x0\",\"type\":\"0x0\"},\"txProof\":[\"0xf851a0eb9c9ef295ba68ff22c85763176dabc05773d58ef77ce34e4a23bf9516c706bc80808080808080a0850e08970f6beee9bd3687c74e591429cf6f65d5faf9db298ddc627ac4a26a1b8080808080808080\",\"0xf86431b861f85f010182a410940000000000000000000000000000000000000000020126a0872929cb57ab6d88d0004a60f00df3dd9e0755860549aea25e559bce3d4a66dba01c06266ee2085ae815c258dd9dbb601bfc08c35c13b7cc9cd4ed88a16c3eb3f0\"],\"receiptProof\":[\"0xf851a0970464c5f98c507970da7d4e5fc0600a9927d9563cf08ed113dc42772e3ff11080808080808080a07e2d58ec5d664555812c48866e548a5e2e5d51dd5b0540d0c59b6c08932e80818080808080808080\",\"0xf9010f31b9010bf901080182a430b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c0\"]},\"id\":67}")]
    public async Task Get_receipt_when_block_has_few_receipts(bool withHeader, string expectedResult)
    {
        IReceiptFinder _receiptFinder = Substitute.For<IReceiptFinder>();
        LogEntry[] logEntries = new[] { Build.A.LogEntry.TestObject, Build.A.LogEntry.TestObject };

        TxReceipt receipt1 = new()
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

        TxReceipt receipt2 = new()
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

        _container.Dispose();
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new ConfigProvider()))
            .AddSingleton<ISpecProvider>(_specProvider)
            .AddSingleton<IBlockTree>(_blockTree)
            .AddSingleton<IReceiptFinder>(_receiptFinder)
            .AddSingleton<IDbProvider>(_dbProvider)
            .AddSingleton<IWorldStateManager>(_worldStateManager)
            .Build();
        _proofRpcModule = _container.Resolve<IRpcModuleFactory<IProofRpcModule>>().Create();
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
        Assert.That(response, Is.EqualTo(expectedResult));
    }

}
