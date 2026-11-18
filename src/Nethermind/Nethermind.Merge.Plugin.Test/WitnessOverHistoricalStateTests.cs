// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading.Tasks;
using Autofac;
using Microsoft.AspNetCore.Http;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Proof;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.SszRest.Handlers;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.History;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

/// <summary>
/// Witness consumers over flat state served from history, which has a block's values but not its trie
/// (real <see cref="HistoricalFlatDbManager"/> classification, mocked persisted boundary).
/// </summary>
public class WitnessOverHistoricalStateTests
{
    private IContainer _container = null!;
    private Block _target = null!;

    [SetUp]
    public void SetUp()
    {
        IPersistenceManager persistence = Substitute.For<IPersistenceManager>();
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = true, HistoryEnabled = true, Layout = FlatLayout.Flat }))
            .AddSingleton(persistence)
            .Build();
        _container.Resolve<IBlockProcessingPauseControl>().Pause();
        IBlockTree blockTree = _container.Resolve<IBlockTree>();
        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        Block parent = Build.A.Block.WithParent(genesis).WithStateRoot(TestItem.KeccakB).TestObject;
        _target = Build.A.Block.WithParent(parent).WithStateRoot(TestItem.KeccakC).TestObject;
        Block head = Build.A.Block.WithParent(_target).WithStateRoot(TestItem.KeccakA).TestObject;
        foreach (Block block in new[] { genesis, parent, _target, head })
        {
            blockTree.SuggestBlock(block);
            blockTree.TryUpdateMainChain(block.Header, wereProcessed: true, forceUpdateHeadBlock: true, block);
        }

        persistence.GetCurrentPersistedStateId().Returns(new StateId(head.Header));
        HistoryAvailability availability = _container.Resolve<HistoryAvailability>();
        byte format = _container.Resolve<HistoryRowFormat>().FormatVersion;
        using (IColumnsWriteBatch<FlatHistoryColumns> batch = _container.Resolve<IColumnsDb<FlatHistoryColumns>>().StartWriteBatch())
        {
            HistoryAvailability.MarkBlock(batch.GetColumnBatch(FlatHistoryColumns.AvailableBlocks), parent.Number, parent.StateRoot!, format);
            HistoryAvailability.MarkBlock(batch.GetColumnBatch(FlatHistoryColumns.AvailableBlocks), _target.Number, _target.StateRoot!, format);
            availability.PublishWatermark(head.Number, format);
        }

        Assert.That(_container.Resolve<IWorldStateManager>().GlobalStateReader.HasStateForBlock(parent.Header), Is.True,
            "precondition: the values are served from history");
    }

    [TearDown]
    public void TearDown() => _container.Dispose();

    [Test]
    public void Proof_call_is_refused_as_not_retained()
    {
        IProofRpcModule proof = _container.Resolve<IRpcModuleFactory<IProofRpcModule>>().Create();

        Assert.That(() => proof.proof_call(new LegacyTransactionForRpc { To = TestItem.AddressB }, new BlockParameter(_target.Number)),
            Throws.TypeOf<StateNotRetainedException>());
    }

    [Test]
    public void Block_witness_generator_returns_null() =>
        Assert.That(CreateGenerator().Generate(_target), Is.Null, "documented contract: null when the parent state is not available");

    [Test]
    public async Task Witness_payload_handler_fails_a_valid_block_whose_witness_cannot_be_generated()
    {
        IEngineRpcModule engine = Substitute.For<IEngineRpcModule>();
        engine.engine_newPayloadV5(Arg.Any<ExecutionPayloadV4>(), Arg.Any<Hash256?[]>(), Arg.Any<Hash256?>(), Arg.Any<byte[][]?>())
            .Returns(ResultWrapper<PayloadStatusV1>.Success(new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = _target.Hash }));
        NewPayloadWithWitnessHandler handler = new(
            new Lazy<IEngineRpcModule>(engine), new WitnessRendezvous(), _container.Resolve<IBlockFinder>(), CreateGenerator());

        using ResultWrapper<NewPayloadWithWitnessV1Result> result = await handler.HandleAsync(
            new ExecutionPayloadParams<ExecutionPayloadV4>(new ExecutionPayloadV4 { BlockHash = _target.Hash! }, [], TestItem.KeccakE, []));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
        }
    }

    [Test]
    public async Task Ssz_payload_with_witness_answers_parent_state_unavailable()
    {
        IEngineRpcModule engine = Substitute.For<IEngineRpcModule>();
        engine.engine_getPayloadV6(Arg.Any<byte[]>()).Returns(ResultWrapper<GetPayloadV6Result?>.Success(
            new GetPayloadV6Result(_target, UInt256.One, new BlobsBundleV2(_target), [], shouldOverrideBuilder: false)));
        DefaultHttpContext ctx = new() { Response = { Body = new MemoryStream() } };

        await new GetPayloadWithWitnessSszHandler(engine, CreateGenerator())
            .HandleAsync(ctx, EngineApiVersions.GetPayload.V6, new byte[8].ToHexString(true).AsMemory(), default);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status500InternalServerError));
    }

    private BlockWitnessGenerator CreateGenerator() => new(
        _container.Resolve<IBlockTree>(),
        _container.Resolve<IWorldStateManager>().GlobalStateReader,
        Array.Empty<IBlockPreprocessorStep>(),
        new Lazy<IWitnessGeneratingBlockProcessingEnvFactory>(() => _container.Resolve<IWitnessGeneratingBlockProcessingEnvFactory>()));
}
