// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
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
using Nethermind.JsonRpc.Modules.Proof;
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
            TestItem.AddressA, new HashSet<UInt256>(), BlockParameter.Earliest);

        result.Result.ResultType.Should().Be(ResultType.Success);
        AccountProofWithMeta payload = result.Data;

        payload.Should().NotBeNull();
        payload.Proof.Should().NotBeNull();
        payload.Proof.Address.Should().Be(TestItem.AddressA);
        payload.Proof.Balance.Should().Be(100_000);
        payload.Proof.Proof.Should().NotBeNull();
        payload.Proof.Proof.Length.Should().BeGreaterThan(0);
    }

    [Test]
    public void Meta_counters_have_sane_values()
    {
        AccountProofWithMeta payload = _proofRpcModule.proof_getProofWithMeta(
            TestItem.AddressA, new HashSet<UInt256>(), BlockParameter.Earliest).Data;

        payload.Meta.Should().NotBeNull();
        payload.Meta.NodeLookups.Should().BeGreaterThan(0,
            "every proof generation must perform at least one node lookup");
        payload.Meta.CacheHits.Should().BeGreaterThanOrEqualTo(0);
        payload.Meta.CacheHits.Should().BeLessThanOrEqualTo(payload.Meta.NodeLookups,
            "cache hits cannot exceed total lookups");
        payload.Meta.MaxDepth.Should().BeGreaterThanOrEqualTo(0);
    }

    [Test]
    public void Repeated_calls_increase_cache_hits_on_warm_path()
    {
        AccountProofWithMeta first = _proofRpcModule.proof_getProofWithMeta(
            TestItem.AddressA, new HashSet<UInt256>(), BlockParameter.Earliest).Data;
        AccountProofWithMeta second = _proofRpcModule.proof_getProofWithMeta(
            TestItem.AddressA, new HashSet<UInt256>(), BlockParameter.Earliest).Data;

        second.Meta.CacheHits.Should().BeGreaterThanOrEqualTo(first.Meta.CacheHits,
            "warming the cache should not reduce hit count on identical query");
    }

    [Test]
    public void Rejects_too_many_storage_keys()
    {
        HashSet<UInt256> storageKeys = new();
        for (int i = 0; i < 1001; i++)
        {
            storageKeys.Add((UInt256)i);
        }

        ResultWrapper<AccountProofWithMeta> result = _proofRpcModule.proof_getProofWithMeta(
            TestItem.AddressA, storageKeys, BlockParameter.Earliest);

        result.Result.ResultType.Should().Be(ResultType.Failure);
        result.ErrorCode.Should().Be(ErrorCodes.InvalidParams);
    }
}
