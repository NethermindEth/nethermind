// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Db;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Proof;
using Nethermind.Serialization.Json;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Proof;

[Parallelizable(ParallelScope.None)]
public class ProofRpcModuleMetaTests
{
    private IProofRpcModule _proofRpcModule = null!;
    private IBlockTree _blockTree = null!;
    private IDbProvider _dbProvider = null!;
    private TestSpecProvider _specProvider = null!;
    private WorldStateManager _worldStateManager = null!;
    private IContainer _container = null!;

    private const int StorageSlotCount = 64;

    [SetUp]
    public async Task Setup()
    {
        _dbProvider = await TestMemDbProvider.InitAsync();
        _worldStateManager = TestWorldStateFactory.CreateWorldStateManagerForTest(_dbProvider, LimboLogs.Instance);

        Hash256 stateRoot;
        IWorldState worldState = new WorldState(_worldStateManager.GlobalWorldState, LimboLogs.Instance);
        using (System.IDisposable _ = worldState.BeginScope(IWorldState.PreGenesis))
        {
            worldState.CreateAccount(TestItem.AddressA, 100_000);
            worldState.CreateAccount(TestItem.AddressB, 200_000);
            worldState.CreateAccount(TestItem.AddressC, 300_000);
            for (int i = 0; i < StorageSlotCount; i++)
            {
                worldState.Set(new StorageCell(TestItem.AddressB, (UInt256)i), [(byte)(i + 1)]);
            }
            worldState.Commit(London.Instance);
            worldState.CommitTree(0);
            stateRoot = worldState.StateRoot;
        }

        InMemoryReceiptStorage receiptStorage = new();
        _specProvider = new TestSpecProvider(London.Instance);
        BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree(new Block(Build.A.BlockHeader.WithStateRoot(stateRoot).TestObject, new BlockBody()), _specProvider)
            .WithTransactions(receiptStorage)
            .OfChainLength(5);
        _blockTree = blockTreeBuilder.TestObject;

        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new ConfigProvider()))
            .AddSingleton<ISpecProvider>(_specProvider)
            .AddSingleton<IBlockPreprocessorStep>(new CompositeBlockPreprocessorStep(new RecoverSignatures(new EthereumEcdsa(TestBlockchainIds.ChainId), _specProvider, LimboLogs.Instance)))
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

    [Test]
    public void Returns_proof_payload_alongside_meta()
    {
        ResultWrapper<AccountProofWithMeta> result = _proofRpcModule.proof_getProofWithMeta(
            TestItem.AddressA, [], BlockParameter.Earliest);

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        AccountProofWithMeta payload = result.Data;

        Assert.That(payload, Is.Not.Null);
        Assert.That(payload.Proof, Is.Not.Null);
        Assert.That(payload.Proof.Address, Is.EqualTo(TestItem.AddressA));
        Assert.That(payload.Proof.Balance, Is.EqualTo((UInt256)100_000));
        Assert.That(payload.Proof.Proof, Is.Not.Null);
        Assert.That(payload.Proof.Proof.Length, Is.GreaterThan(0));
    }

    [Test]
    public void Meta_counters_have_sane_values()
    {
        AccountProofWithMeta payload = _proofRpcModule.proof_getProofWithMeta(
            TestItem.AddressA, [], BlockParameter.Earliest).Data;

        Assert.That(payload.Meta, Is.Not.Null);
        Assert.That(payload.Meta.NodeLookups, Is.GreaterThan(0),
            "every proof generation must perform at least one node lookup");
        Assert.That(payload.Meta.CacheHits, Is.LessThanOrEqualTo(payload.Meta.NodeLookups),
            "cache hits cannot exceed total lookups");
    }

    [Test]
    public void Identical_queries_produce_identical_diagnostics()
    {
        AccountProofWithMeta first = _proofRpcModule.proof_getProofWithMeta(
            TestItem.AddressA, [], BlockParameter.Earliest).Data;
        AccountProofWithMeta second = _proofRpcModule.proof_getProofWithMeta(
            TestItem.AddressA, [], BlockParameter.Earliest).Data;

        Assert.That(second.Meta.NodeLookups, Is.EqualTo(first.Meta.NodeLookups));
        Assert.That(second.Meta.CacheHits, Is.EqualTo(first.Meta.CacheHits));
        Assert.That(second.Meta.MaxDepth, Is.EqualTo(first.Meta.MaxDepth));
    }

    [Test]
    public void MaxDepth_grows_when_storage_trie_is_traversed()
    {
        AccountProofWithMeta withoutStorage = _proofRpcModule.proof_getProofWithMeta(
            TestItem.AddressB, [], BlockParameter.Earliest).Data;

        StorageKeys storageKeys = [];
        for (int i = 0; i < StorageSlotCount; i++)
        {
            storageKeys.Add((UInt256)i);
        }
        AccountProofWithMeta withStorage = _proofRpcModule.proof_getProofWithMeta(
            TestItem.AddressB, storageKeys, BlockParameter.Earliest).Data;

        Assert.That(withStorage.Meta.MaxDepth, Is.GreaterThan(withoutStorage.Meta.MaxDepth),
            "descending into the storage trie reaches deeper than the account trie alone");
        Assert.That(withStorage.Meta.NodeLookups, Is.GreaterThan(withoutStorage.Meta.NodeLookups),
            "storage-trie traversal triggers additional node lookups");
    }

    [Test]
    public void Rejects_too_many_storage_keys()
    {
        StorageKeys storageKeys = [];
        for (int i = 0; i <= EthRpcModule.GetProofStorageKeyLimit; i++)
        {
            storageKeys.Add((UInt256)i);
        }

        ResultWrapper<AccountProofWithMeta> result = _proofRpcModule.proof_getProofWithMeta(
            TestItem.AddressA, storageKeys, BlockParameter.Earliest);

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
    }
}
