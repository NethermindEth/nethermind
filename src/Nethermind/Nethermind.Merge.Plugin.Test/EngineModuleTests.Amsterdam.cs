// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Specs.Forks;
using NSubstitute;
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
        public IEngineRpcModule EngineModule { get; set; }
            = SucceedingEngineModule(new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = TestItem.KeccakA });

        public IWitnessCaptureRegistry Registry { get; set; } = RegistryReturning(MakeStubWitness());

        public NewPayloadWithWitnessHandler Build() =>
            new(new Lazy<IEngineRpcModule>(() => EngineModule), Registry);

        public static IEngineRpcModule SucceedingEngineModule(PayloadStatusV1 status)
        {
            IEngineRpcModule module = Substitute.For<IEngineRpcModule>();
            module
                .engine_newPayloadV5(Arg.Any<ExecutionPayloadV4>(), Arg.Any<byte[]?[]>(), Arg.Any<Hash256?>(), Arg.Any<byte[][]?>())
                .Returns(ResultWrapper<PayloadStatusV1>.Success(status));
            return module;
        }

        public static IEngineRpcModule FailingEngineModule(string error, int errorCode)
        {
            IEngineRpcModule module = Substitute.For<IEngineRpcModule>();
            module
                .engine_newPayloadV5(Arg.Any<ExecutionPayloadV4>(), Arg.Any<byte[]?[]>(), Arg.Any<Hash256?>(), Arg.Any<byte[][]?>())
                .Returns(ResultWrapper<PayloadStatusV1>.Fail(error, errorCode));
            return module;
        }

        public static IWitnessCaptureRegistry RegistryReturning(Witness? witness)
        {
            IWitnessCaptureRegistry registry = Substitute.For<IWitnessCaptureRegistry>();
            registry
                .ArmCapture(Arg.Any<Hash256>())
                .Returns(Task.FromResult(witness));
            return registry;
        }

        public static IWitnessCaptureRegistry RegistryNoop()
        {
            IWitnessCaptureRegistry registry = Substitute.For<IWitnessCaptureRegistry>();
            registry
                .ArmCapture(Arg.Any<Hash256>())
                .Returns(new TaskCompletionSource<Witness?>().Task);
            return registry;
        }
    }

    [Test]
    public async Task NewPayloadWithWitness_valid_status_returns_result_with_executionWitness_populated()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);
        ExecutionPayloadV4 payload = ExecutionPayloadV4.Create(chain.BlockTree.Head!);

        NewPayloadWithWitnessHandler handler = new WitnessHandlerBuilder
        {
            EngineModule = WitnessHandlerBuilder.SucceedingEngineModule(
                new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = TestItem.KeccakA }),
            Registry = WitnessHandlerBuilder.RegistryReturning(MakeStubWitness()),
        }.Build();

        ResultWrapper<NewPayloadWithWitnessV1Result> result =
            await handler.HandleAsync(payload, [], Keccak.Zero, []);

        result.Result.ResultType.Should().Be(ResultType.Success,
            "a VALID status must not produce an RPC-level error");
        result.Data.Status.Should().Be(PayloadStatus.Valid);
        result.Data.LatestValidHash.Should().Be(TestItem.KeccakA);
        result.Data.ExecutionWitness.Should().NotBeNull(
            "a VALID response with successful witness capture must populate executionWitness");
    }

    [Test]
    public async Task NewPayloadWithWitness_valid_status_but_witness_capture_returns_null_yields_success_with_null_witness()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);
        ExecutionPayloadV4 payload = ExecutionPayloadV4.Create(chain.BlockTree.Head!);

        // Null parent forces witness generation to bail out early.
        NewPayloadWithWitnessHandler handler = new WitnessHandlerBuilder
        {
            EngineModule = WitnessHandlerBuilder.SucceedingEngineModule(
                new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = TestItem.KeccakB }),
            Registry = WitnessHandlerBuilder.RegistryReturning(null),
        }.Build();

        ResultWrapper<NewPayloadWithWitnessV1Result> result =
            await handler.HandleAsync(payload, [], Keccak.Zero, []);

        result.Result.ResultType.Should().Be(ResultType.Success,
            "a VALID block must always be accepted even when witness capture fails");
        result.Data.Status.Should().Be(PayloadStatus.Valid,
            "the payload status is independent of witness capture success");
        result.Data.ExecutionWitness.Should().BeNull(
            "executionWitness must be omitted (null) when capture returns null, per spec Union[None, T]");
    }

    [Test]
    public async Task NewPayloadWithWitness_syncing_status_returns_success_with_no_witness()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);
        ExecutionPayloadV4 payload = ExecutionPayloadV4.Create(chain.BlockTree.Head!);

        IWitnessCaptureRegistry registry = WitnessHandlerBuilder.RegistryNoop();
        NewPayloadWithWitnessHandler handler = new WitnessHandlerBuilder
        {
            EngineModule = WitnessHandlerBuilder.SucceedingEngineModule(
                new PayloadStatusV1 { Status = PayloadStatus.Syncing }),
            Registry = registry,
        }.Build();

        ResultWrapper<NewPayloadWithWitnessV1Result> result =
            await handler.HandleAsync(payload, [], Keccak.Zero, []);

        result.Result.ResultType.Should().Be(ResultType.Success);
        result.Data.Status.Should().Be(PayloadStatus.Syncing,
            "SYNCING is a normal processing outcome that must propagate as-is");
        result.Data.ExecutionWitness.Should().BeNull(
            "executionWitness is only populated for VALID status");

        await registry.Received(1).ArmCapture(Arg.Any<Hash256>());
    }

    [Test]
    public async Task NewPayloadWithWitness_invalid_status_returns_success_with_no_witness()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);
        ExecutionPayloadV4 payload = ExecutionPayloadV4.Create(chain.BlockTree.Head!);

        IWitnessCaptureRegistry registry = WitnessHandlerBuilder.RegistryNoop();
        NewPayloadWithWitnessHandler handler = new WitnessHandlerBuilder
        {
            EngineModule = WitnessHandlerBuilder.SucceedingEngineModule(new PayloadStatusV1
            {
                Status = PayloadStatus.Invalid,
                LatestValidHash = TestItem.KeccakD,
                ValidationError = "bad block",
            }),
            Registry = registry,
        }.Build();

        ResultWrapper<NewPayloadWithWitnessV1Result> result =
            await handler.HandleAsync(payload, [], Keccak.Zero, []);

        result.Result.ResultType.Should().Be(ResultType.Success);
        result.Data.Status.Should().Be(PayloadStatus.Invalid);
        result.Data.LatestValidHash.Should().Be(TestItem.KeccakD);
        result.Data.ValidationError.Should().Be("bad block");
        result.Data.ExecutionWitness.Should().BeNull(
            "executionWitness must be omitted for INVALID status");
    }

    [Test]
    public async Task NewPayloadWithWitness_engine_newPayloadV5_fails_propagates_error_code_and_message()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);
        ExecutionPayloadV4 payload = ExecutionPayloadV4.Create(chain.BlockTree.Head!);

        NewPayloadWithWitnessHandler handler = new WitnessHandlerBuilder
        {
            EngineModule = WitnessHandlerBuilder.FailingEngineModule("Unsupported fork", MergeErrorCodes.UnsupportedFork),
            Registry = WitnessHandlerBuilder.RegistryNoop(),
        }.Build();

        ResultWrapper<NewPayloadWithWitnessV1Result> result =
            await handler.HandleAsync(payload, [], Keccak.Zero, []);

        result.Result.ResultType.Should().Be(ResultType.Failure,
            "an RPC-level failure from engine_newPayloadV5 must propagate as an RPC failure");
        result.ErrorCode.Should().Be(MergeErrorCodes.UnsupportedFork,
            "the error code must be preserved so callers can distinguish UnsupportedFork from other errors");
        result.Result.Error.Should().Contain("Unsupported fork");
    }
}
