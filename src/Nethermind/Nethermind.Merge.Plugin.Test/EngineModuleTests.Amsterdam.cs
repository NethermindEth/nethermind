// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
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

    private sealed class WitnessHandlerBuilder
    {
        public Func<ExecutionPayloadV4, byte[]?[], Hash256?, byte[][]?, Task<ResultWrapper<PayloadStatusV1>>> NewPayloadV5 { get; set; }
            = SucceedingNewPayloadV5(new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = TestItem.KeccakA });

        public IBlockTree BlockTree { get; set; } = BlockTreeWithHeader(Nethermind.Core.Test.Builders.Build.A.BlockHeader.TestObject);

        public IWitnessGeneratingBlockProcessingEnvFactory WitnessFactory { get; set; } =
            WitnessFactoryFor(MakeStubWitness());

        public NewPayloadWithWitnessHandler Build() =>
            new(NewPayloadV5, BlockTree, WitnessFactory, LimboLogs.Instance);

        public static Func<ExecutionPayloadV4, byte[]?[], Hash256?, byte[][]?, Task<ResultWrapper<PayloadStatusV1>>>
            SucceedingNewPayloadV5(PayloadStatusV1 status) =>
            (_, _, _, _) => Task.FromResult(ResultWrapper<PayloadStatusV1>.Success(status));

        public static Func<ExecutionPayloadV4, byte[]?[], Hash256?, byte[][]?, Task<ResultWrapper<PayloadStatusV1>>>
            FailingNewPayloadV5(string error, int errorCode) =>
            (_, _, _, _) => Task.FromResult(ResultWrapper<PayloadStatusV1>.Fail(error, errorCode));

        public static IBlockTree BlockTreeWithHeader(BlockHeader? header)
        {
            IBlockTree bt = Substitute.For<IBlockTree>();
            bt.FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>())
              .Returns(header);
            return bt;
        }

        private static IWitnessGeneratingBlockProcessingEnvFactory BuildWitnessFactory(
            Action<IExistingBlockWitnessCollector> configureCollector)
        {
            IExistingBlockWitnessCollector collector = Substitute.For<IExistingBlockWitnessCollector>();
            configureCollector(collector);

            IWitnessGeneratingBlockProcessingEnv env =
                Substitute.For<IWitnessGeneratingBlockProcessingEnv>();
            env.CreateExistingBlockWitnessCollector().Returns(collector);

            IWitnessGeneratingBlockProcessingEnvScope scope =
                Substitute.For<IWitnessGeneratingBlockProcessingEnvScope>();
            scope.Env.Returns(env);

            IWitnessGeneratingBlockProcessingEnvFactory factory =
                Substitute.For<IWitnessGeneratingBlockProcessingEnvFactory>();
            factory.CreateScope().Returns(scope);

            return factory;
        }

        public static IWitnessGeneratingBlockProcessingEnvFactory WitnessFactoryFor(Witness? witness) =>
            BuildWitnessFactory(collector =>
                collector
                    .GetWitnessForExistingBlock(Arg.Any<BlockHeader>(), Arg.Any<Block>())
                    .Returns(witness));

        public static IWitnessGeneratingBlockProcessingEnvFactory ThrowingWitnessFactory(Exception ex) =>
            BuildWitnessFactory(collector =>
                collector
                    .GetWitnessForExistingBlock(Arg.Any<BlockHeader>(), Arg.Any<Block>())
                    .Throws(ex));

        public static IWitnessGeneratingBlockProcessingEnvFactory NoopWitnessFactory() =>
            Substitute.For<IWitnessGeneratingBlockProcessingEnvFactory>();
    }

    [Test]
    public async Task NewPayloadWithWitness_valid_status_returns_result_with_executionWitness_populated()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);
        ExecutionPayloadV4 payload = ExecutionPayloadV4.Create(chain.BlockTree.Head!);

        Witness stubWitness = MakeStubWitness();
        NewPayloadWithWitnessHandler handler = new WitnessHandlerBuilder
        {
            NewPayloadV5 = WitnessHandlerBuilder.SucceedingNewPayloadV5(
                new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = TestItem.KeccakA }),
            BlockTree = WitnessHandlerBuilder.BlockTreeWithHeader(Nethermind.Core.Test.Builders.Build.A.BlockHeader.TestObject),
            WitnessFactory = WitnessHandlerBuilder.WitnessFactoryFor(stubWitness),
        }.Build();

        ResultWrapper<NewPayloadWithWitnessV1Result> result =
            await handler.HandleAsync(payload, [], Keccak.Zero, []);

        result.Result.ResultType.Should().Be(ResultType.Success,
            "a VALID status must not produce an RPC-level error");
        result.Data.Status.Should().Be(PayloadStatus.Valid);
        result.Data.LatestValidHash.Should().Be(TestItem.KeccakA);
        result.Data.ExecutionWitness.Should().NotBeNull(
            "a VALID response with successful witness generation must populate executionWitness");
    }

    [Test]
    public async Task NewPayloadWithWitness_valid_status_but_witness_generation_fails_returns_success_with_null_witness()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);
        ExecutionPayloadV4 payload = ExecutionPayloadV4.Create(chain.BlockTree.Head!);

        // Null parent forces witness generation to bail out early.
        NewPayloadWithWitnessHandler handler = new WitnessHandlerBuilder
        {
            NewPayloadV5 = WitnessHandlerBuilder.SucceedingNewPayloadV5(
                new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = TestItem.KeccakB }),
            BlockTree = WitnessHandlerBuilder.BlockTreeWithHeader(null),
            WitnessFactory = WitnessHandlerBuilder.NoopWitnessFactory(),
        }.Build();

        ResultWrapper<NewPayloadWithWitnessV1Result> result =
            await handler.HandleAsync(payload, [], Keccak.Zero, []);

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
        ExecutionPayloadV4 payload = ExecutionPayloadV4.Create(chain.BlockTree.Head!);

        NewPayloadWithWitnessHandler handler = new WitnessHandlerBuilder
        {
            NewPayloadV5 = WitnessHandlerBuilder.SucceedingNewPayloadV5(
                new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = TestItem.KeccakC }),
            BlockTree = WitnessHandlerBuilder.BlockTreeWithHeader(Nethermind.Core.Test.Builders.Build.A.BlockHeader.TestObject),
            WitnessFactory = WitnessHandlerBuilder.ThrowingWitnessFactory(
                new InvalidOperationException("simulated witness failure")),
        }.Build();

        ResultWrapper<NewPayloadWithWitnessV1Result> result =
            await handler.HandleAsync(payload, [], Keccak.Zero, []);

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
        ExecutionPayloadV4 payload = ExecutionPayloadV4.Create(chain.BlockTree.Head!);

        IWitnessGeneratingBlockProcessingEnvFactory witnessFactory = WitnessHandlerBuilder.NoopWitnessFactory();
        NewPayloadWithWitnessHandler handler = new WitnessHandlerBuilder
        {
            NewPayloadV5 = WitnessHandlerBuilder.SucceedingNewPayloadV5(new PayloadStatusV1 { Status = PayloadStatus.Syncing }),
            BlockTree = WitnessHandlerBuilder.BlockTreeWithHeader(Nethermind.Core.Test.Builders.Build.A.BlockHeader.TestObject),
            WitnessFactory = witnessFactory,
        }.Build();

        ResultWrapper<NewPayloadWithWitnessV1Result> result =
            await handler.HandleAsync(payload, [], Keccak.Zero, []);

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
        ExecutionPayloadV4 payload = ExecutionPayloadV4.Create(chain.BlockTree.Head!);

        IWitnessGeneratingBlockProcessingEnvFactory witnessFactory = WitnessHandlerBuilder.NoopWitnessFactory();
        NewPayloadWithWitnessHandler handler = new WitnessHandlerBuilder
        {
            NewPayloadV5 = WitnessHandlerBuilder.SucceedingNewPayloadV5(new PayloadStatusV1
            {
                Status = PayloadStatus.Invalid,
                LatestValidHash = TestItem.KeccakD,
                ValidationError = "bad block"
            }),
            BlockTree = WitnessHandlerBuilder.BlockTreeWithHeader(Nethermind.Core.Test.Builders.Build.A.BlockHeader.TestObject),
            WitnessFactory = witnessFactory,
        }.Build();

        ResultWrapper<NewPayloadWithWitnessV1Result> result =
            await handler.HandleAsync(payload, [], Keccak.Zero, []);

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
        ExecutionPayloadV4 payload = ExecutionPayloadV4.Create(chain.BlockTree.Head!);

        IWitnessGeneratingBlockProcessingEnvFactory witnessFactory = WitnessHandlerBuilder.NoopWitnessFactory();
        NewPayloadWithWitnessHandler handler = new WitnessHandlerBuilder
        {
            NewPayloadV5 = WitnessHandlerBuilder.FailingNewPayloadV5("Unsupported fork", MergeErrorCodes.UnsupportedFork),
            BlockTree = WitnessHandlerBuilder.BlockTreeWithHeader(Nethermind.Core.Test.Builders.Build.A.BlockHeader.TestObject),
            WitnessFactory = witnessFactory,
        }.Build();

        ResultWrapper<NewPayloadWithWitnessV1Result> result =
            await handler.HandleAsync(payload, [], Keccak.Zero, []);

        result.Result.ResultType.Should().Be(ResultType.Failure,
            "an RPC-level failure from engine_newPayloadV5 must propagate as an RPC failure");
        result.ErrorCode.Should().Be(MergeErrorCodes.UnsupportedFork,
            "the error code must be preserved so callers can distinguish UnsupportedFork from other errors");
        result.Result.Error.Should().Contain("Unsupported fork");

        witnessFactory.DidNotReceive().CreateScope();
    }
}
