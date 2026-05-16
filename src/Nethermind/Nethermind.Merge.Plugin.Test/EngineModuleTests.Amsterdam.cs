// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.GC;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Specs.Forks;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    private static Witness MakeStubWitness() =>
        new()
        {
            State = new ArrayPoolList<byte[]>(1) { new byte[] { 0xDE, 0xAD } },
            Codes = new ArrayPoolList<byte[]>(0),
            Keys = new ArrayPoolList<byte[]>(0),
            Headers = new ArrayPoolList<byte[]>(0),
        };

    private sealed class StubbedEngineRpcModule(
        PayloadStatusV1 stubbedV5Status,
        IBlockTree blockTree,
        IWitnessGeneratingBlockProcessingEnvFactory witnessEnvFactory,
        MergeTestBlockchain chain)
        : EngineRpcModule(
            Substitute.For<IAsyncHandler<byte[], ExecutionPayload?>>(),
            Substitute.For<IAsyncHandler<byte[], GetPayloadV2Result?>>(),
            Substitute.For<IAsyncHandler<byte[], GetPayloadV3Result?>>(),
            Substitute.For<IAsyncHandler<byte[], GetPayloadV4Result?>>(),
            Substitute.For<IAsyncHandler<byte[], GetPayloadV5Result?>>(),
            Substitute.For<IAsyncHandler<byte[], GetPayloadV6Result?>>(),
            Substitute.For<IAsyncHandler<ExecutionPayload, PayloadStatusV1>>(),
            Substitute.For<IForkchoiceUpdatedHandler>(),
            Substitute.For<IHandler<IReadOnlyList<Hash256>, IReadOnlyList<ExecutionPayloadBodyV1Result?>>>(),
            Substitute.For<IGetPayloadBodiesByRangeV1Handler>(),
            Substitute.For<IHandler<TransitionConfigurationV1, TransitionConfigurationV1>>(),
            Substitute.For<IHandler<IEnumerable<string>, IReadOnlyList<string>>>(),
            Substitute.For<IAsyncHandler<byte[][], IReadOnlyList<BlobAndProofV1?>>>(),
            Substitute.For<IAsyncHandler<GetBlobsHandlerV2Request, IReadOnlyList<BlobAndProofV2?>?>>(),
            Substitute.For<IHandler<IReadOnlyList<Hash256>, IReadOnlyList<ExecutionPayloadBodyV2Result?>>>(),
            Substitute.For<IGetPayloadBodiesByRangeV2Handler>(),
            Substitute.For<IEngineRequestsTracker>(),
            chain.SpecProvider,
            new GCKeeper(NoGCStrategy.Instance, chain.LogManager),
            blockTree,
            witnessEnvFactory,
            LimboLogs.Instance)
    {
        private readonly PayloadStatusV1 _stubbedV5Status = stubbedV5Status;

        public override Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV5(
            ExecutionPayloadV4 executionPayload,
            byte[]?[] blobVersionedHashes,
            Hash256? parentBeaconBlockRoot,
            byte[][]? executionRequests)
            => Task.FromResult(ResultWrapper<PayloadStatusV1>.Success(_stubbedV5Status));
    }

    [Test]
    public async Task NewPayloadWithWitness_valid_status_returns_result_with_executionWitness_populated()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);

        PayloadStatusV1 status = new() { Status = PayloadStatus.Valid, LatestValidHash = TestItem.KeccakA };

        Witness stubWitness = MakeStubWitness();
        IExistingBlockWitnessCollector stubCollector = Substitute.For<IExistingBlockWitnessCollector>();
        stubCollector
            .GetWitnessForExistingBlock(Arg.Any<BlockHeader>(), Arg.Any<Block>())
            .Returns(stubWitness);

        IWitnessGeneratingBlockProcessingEnv stubEnv = Substitute.For<IWitnessGeneratingBlockProcessingEnv>();
        stubEnv.CreateExistingBlockWitnessCollector().Returns(stubCollector);

        IWitnessGeneratingBlockProcessingEnvScope stubScope =
            Substitute.For<IWitnessGeneratingBlockProcessingEnvScope>();
        stubScope.Env.Returns(stubEnv);

        IWitnessGeneratingBlockProcessingEnvFactory witnessFactory =
            Substitute.For<IWitnessGeneratingBlockProcessingEnvFactory>();
        witnessFactory.CreateScope().Returns(stubScope);

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree
            .FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>())
            .Returns(Build.A.BlockHeader.TestObject);

        StubbedEngineRpcModule module = new(status, blockTree, witnessFactory, chain);

        // Build a minimal valid ExecutionPayloadV4 from the genesis block so TryGetBlock succeeds.
        ExecutionPayloadV4 payload = ExecutionPayloadV4.Create(chain.BlockTree.Head!);

        ResultWrapper<NewPayloadWithWitnessV1Result> result =
            await module.engine_newPayloadWithWitness(payload, [], Keccak.Zero, []);

        result.Result.ResultType.Should().Be(ResultType.Success, "a VALID status must not produce an RPC-level error");
        result.Data.Status.Should().Be(PayloadStatus.Valid);
        result.Data.LatestValidHash.Should().Be(TestItem.KeccakA);
        result.Data.ExecutionWitness.Should().NotBeNull(
            "a VALID response with successful witness generation must populate executionWitness");
    }

    [Test]
    public async Task NewPayloadWithWitness_valid_status_but_witness_generation_fails_returns_success_with_null_witness()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);

        PayloadStatusV1 status = new() { Status = PayloadStatus.Valid, LatestValidHash = TestItem.KeccakB };

        // Return null parent so witness generation bails out early.
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree
            .FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>())
            .Returns((BlockHeader?)null);

        IWitnessGeneratingBlockProcessingEnvFactory witnessFactory =
            Substitute.For<IWitnessGeneratingBlockProcessingEnvFactory>();

        StubbedEngineRpcModule module = new(status, blockTree, witnessFactory, chain);

        ExecutionPayloadV4 payload = ExecutionPayloadV4.Create(chain.BlockTree.Head!);

        ResultWrapper<NewPayloadWithWitnessV1Result> result =
            await module.engine_newPayloadWithWitness(payload, [], Keccak.Zero, []);

        result.Result.ResultType.Should().Be(ResultType.Success,
            "a VALID block must always be accepted even when witness generation fails");
        result.Data.Status.Should().Be(PayloadStatus.Valid,
            "the payload status itself is independent of witness generation success");
        result.Data.ExecutionWitness.Should().BeNull(
            "executionWitness must be omitted (null) when witness generation fails, per spec Union[None, T]");
    }

    [Test]
    public async Task NewPayloadWithWitness_valid_status_witness_collector_throws_returns_success_with_null_witness()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);

        PayloadStatusV1 status = new() { Status = PayloadStatus.Valid, LatestValidHash = TestItem.KeccakC };

        IExistingBlockWitnessCollector stubCollector = Substitute.For<IExistingBlockWitnessCollector>();
        stubCollector
            .GetWitnessForExistingBlock(Arg.Any<BlockHeader>(), Arg.Any<Block>())
            .Throws(new InvalidOperationException("simulated witness failure"));

        IWitnessGeneratingBlockProcessingEnv stubEnv = Substitute.For<IWitnessGeneratingBlockProcessingEnv>();
        stubEnv.CreateExistingBlockWitnessCollector().Returns(stubCollector);

        IWitnessGeneratingBlockProcessingEnvScope stubScope =
            Substitute.For<IWitnessGeneratingBlockProcessingEnvScope>();
        stubScope.Env.Returns(stubEnv);

        IWitnessGeneratingBlockProcessingEnvFactory witnessFactory =
            Substitute.For<IWitnessGeneratingBlockProcessingEnvFactory>();
        witnessFactory.CreateScope().Returns(stubScope);

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree
            .FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>())
            .Returns(Build.A.BlockHeader.TestObject);

        StubbedEngineRpcModule module = new(status, blockTree, witnessFactory, chain);

        ExecutionPayloadV4 payload = ExecutionPayloadV4.Create(chain.BlockTree.Head!);

        ResultWrapper<NewPayloadWithWitnessV1Result> result =
            await module.engine_newPayloadWithWitness(payload, [], Keccak.Zero, []);

        result.Result.ResultType.Should().Be(ResultType.Success,
            "exceptions in witness generation must not surface as RPC errors");
        result.Data.Status.Should().Be(PayloadStatus.Valid);
        result.Data.ExecutionWitness.Should().BeNull(
            "a thrown exception during witness generation must yield witness=null");
    }

    [Test]
    public async Task NewPayloadWithWitness_syncing_status_returns_success_with_no_witness()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);

        PayloadStatusV1 status = new() { Status = PayloadStatus.Syncing };

        IWitnessGeneratingBlockProcessingEnvFactory witnessFactory =
            Substitute.For<IWitnessGeneratingBlockProcessingEnvFactory>();
        IBlockTree blockTree = Substitute.For<IBlockTree>();

        StubbedEngineRpcModule module = new(status, blockTree, witnessFactory, chain);

        ExecutionPayloadV4 payload = ExecutionPayloadV4.Create(chain.BlockTree.Head!);

        ResultWrapper<NewPayloadWithWitnessV1Result> result =
            await module.engine_newPayloadWithWitness(payload, [], Keccak.Zero, []);

        result.Result.ResultType.Should().Be(ResultType.Success);
        result.Data.Status.Should().Be(PayloadStatus.Syncing,
            "SYNCING is a normal processing outcome that must propagate as-is");
        result.Data.ExecutionWitness.Should().BeNull(
            "executionWitness is only populated for VALID status");

        // Witness generation must not be attempted for non-VALID status.
        witnessFactory.DidNotReceive().CreateScope();
    }

    [Test]
    public async Task NewPayloadWithWitness_invalid_status_returns_success_with_no_witness()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);

        PayloadStatusV1 status = new()
        {
            Status = PayloadStatus.Invalid,
            LatestValidHash = TestItem.KeccakD,
            ValidationError = "bad block"
        };

        IWitnessGeneratingBlockProcessingEnvFactory witnessFactory =
            Substitute.For<IWitnessGeneratingBlockProcessingEnvFactory>();
        IBlockTree blockTree = Substitute.For<IBlockTree>();

        StubbedEngineRpcModule module = new(status, blockTree, witnessFactory, chain);

        ExecutionPayloadV4 payload = ExecutionPayloadV4.Create(chain.BlockTree.Head!);

        ResultWrapper<NewPayloadWithWitnessV1Result> result =
            await module.engine_newPayloadWithWitness(payload, [], Keccak.Zero, []);

        result.Result.ResultType.Should().Be(ResultType.Success);
        result.Data.Status.Should().Be(PayloadStatus.Invalid);
        result.Data.LatestValidHash.Should().Be(TestItem.KeccakD);
        result.Data.ValidationError.Should().Be("bad block");
        result.Data.ExecutionWitness.Should().BeNull(
            "executionWitness must be omitted for INVALID status");

        witnessFactory.DidNotReceive().CreateScope();
    }

    [Test]
    public async Task NewPayloadWithWitness_engine_newPayloadV5_fails_propagates_error_code_and_message()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);

        // Simulate the engine returning an UnsupportedFork error (e.g. pre-Amsterdam payload
        // sent to the Amsterdam handler).
        IWitnessGeneratingBlockProcessingEnvFactory witnessFactory =
            Substitute.For<IWitnessGeneratingBlockProcessingEnvFactory>();
        IBlockTree blockTree = Substitute.For<IBlockTree>();

        FailingNewPayloadEngineRpcModule failModule = new(
            "Unsupported fork", MergeErrorCodes.UnsupportedFork, blockTree, witnessFactory, chain);

        ExecutionPayloadV4 payload = ExecutionPayloadV4.Create(chain.BlockTree.Head!);

        ResultWrapper<NewPayloadWithWitnessV1Result> result =
            await failModule.engine_newPayloadWithWitness(payload, [], Keccak.Zero, []);

        result.Result.ResultType.Should().Be(ResultType.Failure,
            "an RPC-level failure from engine_newPayloadV5 must propagate as an RPC failure");
        result.ErrorCode.Should().Be(MergeErrorCodes.UnsupportedFork,
            "the error code must be preserved so callers can distinguish UnsupportedFork from other errors");
        result.Result.Error.Should().Contain("Unsupported fork");

        witnessFactory.DidNotReceive().CreateScope();
    }

    private sealed class FailingNewPayloadEngineRpcModule(
        string error,
        int errorCode,
        IBlockTree blockTree,
        IWitnessGeneratingBlockProcessingEnvFactory witnessEnvFactory,
        MergeTestBlockchain chain)
        : EngineRpcModule(
            Substitute.For<IAsyncHandler<byte[], ExecutionPayload?>>(),
            Substitute.For<IAsyncHandler<byte[], GetPayloadV2Result?>>(),
            Substitute.For<IAsyncHandler<byte[], GetPayloadV3Result?>>(),
            Substitute.For<IAsyncHandler<byte[], GetPayloadV4Result?>>(),
            Substitute.For<IAsyncHandler<byte[], GetPayloadV5Result?>>(),
            Substitute.For<IAsyncHandler<byte[], GetPayloadV6Result?>>(),
            Substitute.For<IAsyncHandler<ExecutionPayload, PayloadStatusV1>>(),
            Substitute.For<IForkchoiceUpdatedHandler>(),
            Substitute.For<IHandler<IReadOnlyList<Hash256>, IReadOnlyList<ExecutionPayloadBodyV1Result?>>>(),
            Substitute.For<IGetPayloadBodiesByRangeV1Handler>(),
            Substitute.For<IHandler<TransitionConfigurationV1, TransitionConfigurationV1>>(),
            Substitute.For<IHandler<IEnumerable<string>, IReadOnlyList<string>>>(),
            Substitute.For<IAsyncHandler<byte[][], IReadOnlyList<BlobAndProofV1?>>>(),
            Substitute.For<IAsyncHandler<GetBlobsHandlerV2Request, IReadOnlyList<BlobAndProofV2?>?>>(),
            Substitute.For<IHandler<IReadOnlyList<Hash256>, IReadOnlyList<ExecutionPayloadBodyV2Result?>>>(),
            Substitute.For<IGetPayloadBodiesByRangeV2Handler>(),
            Substitute.For<IEngineRequestsTracker>(),
            chain.SpecProvider,
            new GCKeeper(NoGCStrategy.Instance, chain.LogManager),
            blockTree,
            witnessEnvFactory,
            LimboLogs.Instance)
    {
        private readonly string _error = error;
        private readonly int _errorCode = errorCode;

        public override Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV5(
            ExecutionPayloadV4 executionPayload,
            byte[]?[] blobVersionedHashes,
            Hash256? parentBeaconBlockRoot,
            byte[][]? executionRequests)
            => Task.FromResult(ResultWrapper<PayloadStatusV1>.Fail(_error, _errorCode));
    }
}
