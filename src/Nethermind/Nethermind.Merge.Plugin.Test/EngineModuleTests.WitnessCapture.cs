// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Nethermind.Blockchain.Headers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    private static Witness MakeStubWitness() => new()
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

        public WitnessCapturingWorldStateProxy? Proxy { get; set; } = MakeUnarmedProxy();

        public NewPayloadWithWitnessHandler Build() =>
            new(new Lazy<IEngineRpcModule>(() => EngineModule), new WitnessProxyResolver(Proxy));

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
    }

    [Test]
    [Category("WitnessCapture")]
    public void Proxy_RequestWitness_returns_incomplete_task_before_drain()
    {
        WitnessCapturingWorldStateProxy proxy = MakeUnarmedProxy();

        Task<Witness?> task = proxy.RequestWitness(TestItem.KeccakA);

        task.IsCompleted.Should().BeFalse(
            "the task must remain pending until the block-processor decorator drains the capture");
    }

    [Test]
    [Category("WitnessCapture")]
    public void Proxy_CancelWitnessRequest_cancels_TCS_and_removes_entry()
    {
        WitnessCapturingWorldStateProxy proxy = MakeUnarmedProxy();
        Hash256 hash = TestItem.KeccakD;

        Task<Witness?> captureTask = proxy.RequestWitness(hash);
        proxy.HasPendingRequest(hash).Should().BeTrue();

        proxy.CancelWitnessRequest(hash);

        proxy.HasPendingRequest(hash).Should().BeFalse(
            "CancelWitnessRequest must remove the entry");
        captureTask.IsCanceled.Should().BeTrue(
            "CancelWitnessRequest must cancel the TCS so any awaiter gets OperationCanceledException");
    }

    [Test]
    [Category("WitnessCapture")]
    public void Proxy_CancelWitnessRequest_noop_when_no_entry_exists()
    {
        WitnessCapturingWorldStateProxy proxy = MakeUnarmedProxy();
        Action cancel = () => proxy.CancelWitnessRequest(Keccak.Zero);
        cancel.Should().NotThrow("cancelling a non-existent request is a valid no-op");
    }

    [Test]
    [Category("WitnessCapture")]
    public void Proxy_duplicate_RequestWitness_cancels_previous_TCS()
    {
        WitnessCapturingWorldStateProxy proxy = MakeUnarmedProxy();
        Hash256 hash = TestItem.KeccakE;

        Task<Witness?> first = proxy.RequestWitness(hash);
        Task<Witness?> second = proxy.RequestWitness(hash);

        first.IsCanceled.Should().BeTrue(
            "the orphaned TCS must be cancelled so any awaiter gets OperationCanceledException rather than hanging forever");
        second.IsCompleted.Should().BeFalse("the replacement TCS is still pending");
    }

    [Test]
    [Category("WitnessCapture")]
    public void Proxy_unarmed_BuildWitness_returns_null()
    {
        WitnessCapturingWorldStateProxy proxy = MakeUnarmedProxy();
        Witness? result = proxy.BuildWitness(Keccak.EmptyTreeHash, TestItem.KeccakA, 0);
        result.Should().BeNull("BuildWitness must return null when the proxy was never armed");
    }

    [Test]
    [Category("WitnessCapture")]
    public void Proxy_nested_Arm_throws_InvalidOperationException()
    {
        WitnessCapturingWorldStateProxy proxy = MakeUnarmedProxy();
        proxy.Arm();

        Action nestedArm = () => proxy.Arm();
        nestedArm.Should().Throw<InvalidOperationException>("nested arming is explicitly disallowed");
    }

    [Test]
    [Category("WitnessCapture")]
    public void Proxy_BuildWitness_Disarm_then_second_Arm_succeeds()
    {
        WitnessCapturingWorldStateProxy proxy = MakeUnarmedProxy();

        proxy.Arm();
        proxy.TryGetAccount(TestItem.AddressA, out _);
        proxy.BuildWitness(Keccak.EmptyTreeHash, TestItem.KeccakA, 0);
        proxy.Disarm();

        Action secondArm = () => proxy.Arm();
        secondArm.Should().NotThrow("a second Arm after BuildWitness consumes the collections must succeed");
    }

    [Test]
    [Category("WitnessCapture")]
    public void Proxy_storage_slot_writes_and_reads_are_recorded()
    {
        IStateReader reader = Substitute.For<IStateReader>();
        WitnessCapturingWorldStateProxy proxy = MakeUnarmedProxy(stateReader: reader);
        proxy.Arm();

        StorageCell writeCell = new(TestItem.AddressA, UInt256.One);
        StorageCell readCell = new(TestItem.AddressB, UInt256.MaxValue);
        proxy.Set(writeCell, [0x01]);
        proxy.Set(readCell, [0x02]);

        Witness? witness = proxy.BuildWitness(Keccak.EmptyTreeHash, TestItem.KeccakA, 0);
        proxy.Disarm();

        reader.Received(3).RunTreeVisitor(
            Arg.Any<AccountProofCollector>(),
            Arg.Any<BlockHeader?>(),
            Arg.Any<VisitingOptions?>());
        witness.Should().NotBeNull();
    }

    [Test]
    [Category("WitnessCapture")]
    public void Proxy_GetCode_records_bytecode_in_Witness_Codes()
    {
        byte[] code = [0x60, 0x00, 0x56];
        IWorldState inner = Substitute.For<IWorldState>();
        inner.GetCode(Arg.Any<Address>()).Returns(code);
        inner.StateRoot.Returns(Keccak.EmptyTreeHash);

        IHeaderFinder finder = Substitute.For<IHeaderFinder>();
        finder.Get(Arg.Any<Hash256>(), Arg.Any<long?>()).Returns(Build.A.BlockHeader.TestObject);

        WitnessCapturingWorldStateProxy proxy = new(inner, Substitute.For<IStateReader>(), finder, LimboLogs.Instance);
        proxy.Arm();
        proxy.GetCode(TestItem.AddressA);

        Witness? witness = proxy.BuildWitness(Keccak.EmptyTreeHash, TestItem.KeccakA, 0);
        proxy.Disarm();

        witness.Should().NotBeNull();
        witness!.Codes.Count.Should().Be(1,
            "the bytecode returned by GetCode must appear in Witness.Codes");
        witness.Codes[0].Should().BeEquivalentTo(code);
    }

    [Test]
    [Category("WitnessCapture")]
    public void Proxy_unarmed_state_accesses_do_not_record_anything()
    {
        WitnessCapturingWorldStateProxy proxy = MakeUnarmedProxy();

        proxy.TryGetAccount(TestItem.AddressA, out _);
        proxy.IsContract(TestItem.AddressA);
        proxy.Set(new StorageCell(TestItem.AddressA, UInt256.One), [0xFF]);

        Witness? w = proxy.BuildWitness(Keccak.EmptyTreeHash, TestItem.KeccakA, 0);
        w.Should().BeNull("BuildWitness must return null because collections were never allocated");
    }

    [Test]
    [Category("WitnessCapture")]
    public async Task BranchProcessor_registry_task_is_complete_before_newPayloadV5_returns()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);
        WitnessCapturingWorldStateProxy proxy = (WitnessCapturingWorldStateProxy)chain.MainWorldState;

        (ExecutionPayloadV4 payload, byte[][]? requests) = await BuildAmsterdamPayload(chain);
        Hash256 hash = payload.BlockHash!;

        Task<Witness?> captureTask = proxy.RequestWitness(hash);

        await chain.EngineRpcModule.engine_newPayloadV5(payload, [], TestItem.KeccakE, requests ?? []);

        captureTask.IsCompleted.Should().BeTrue(
            "BranchProcessor must complete the TCS synchronously inside ProcessOne (after CommitTree) " +
            "before engine_newPayloadV5 returns, so the handler's await is a non-blocking retrieval");

        using Witness? witness = await captureTask;
        witness.Should().NotBeNull("a VALID block must produce a non-null witness");
    }

    [Test]
    [Category("WitnessCapture")]
    public async Task BranchProcessor_does_not_arm_proxy_for_blocks_not_in_registry()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);

        WitnessCapturingWorldStateProxy proxy =
            (WitnessCapturingWorldStateProxy)chain.MainWorldState;

        (ExecutionPayloadV4 payload, byte[][]? requests) = await BuildAmsterdamPayload(chain);

        await chain.EngineRpcModule.engine_newPayloadV5(payload, [], TestItem.KeccakE, requests ?? []);

        BlockHeader head = chain.BlockTree.Head!.Header;
        Witness? stray = proxy.BuildWitness(head.StateRoot!, head.Hash!, head.Number);
        stray.Should().BeNull(
            "without arming, BuildWitness must return null — tracking collections were never allocated");
    }

    [Test]
    [Category("WitnessCapture")]
    public async Task BranchProcessor_multi_block_branch_captures_independent_witnesses()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        WitnessCapturingWorldStateProxy proxy = (WitnessCapturingWorldStateProxy)chain.MainWorldState;

        (ExecutionPayloadV4 p1, byte[][]? r1) = await BuildAmsterdamPayload(chain);
        Task<Witness?> t1 = proxy.RequestWitness(p1.BlockHash!);
        await rpc.engine_newPayloadV5(p1, [], TestItem.KeccakE, r1 ?? []);
        await rpc.engine_forkchoiceUpdatedV4(
            new ForkchoiceStateV1(p1.BlockHash!, p1.BlockHash!, p1.BlockHash!), null);
        (await t1)?.Dispose();

        (ExecutionPayloadV4 p2, byte[][]? r2) = await BuildAmsterdamPayload(chain);
        Task<Witness?> t2 = proxy.RequestWitness(p2.BlockHash!);
        await rpc.engine_newPayloadV5(p2, [], TestItem.KeccakE, r2 ?? []);

        t1.IsCompletedSuccessfully.Should().BeTrue("block-1 task was completed during block-1");
        t2.IsCompletedSuccessfully.Should().BeTrue("block-2 task must be completed during block-2");

        using Witness? w2 = await t2;
        w2.Should().NotBeNull("block 2 must produce a valid witness");
    }

    [Test]
    [Category("WitnessCapture")]
    public async Task BranchProcessor_unarmed_block_between_two_armed_blocks_leaves_proxy_clean()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        WitnessCapturingWorldStateProxy proxy = (WitnessCapturingWorldStateProxy)chain.MainWorldState;

        (ExecutionPayloadV4 p1, byte[][]? r1) = await BuildAmsterdamPayload(chain);
        Task<Witness?> t1 = proxy.RequestWitness(p1.BlockHash!);
        await rpc.engine_newPayloadV5(p1, [], TestItem.KeccakE, r1 ?? []);
        await rpc.engine_forkchoiceUpdatedV4(new ForkchoiceStateV1(p1.BlockHash!, p1.BlockHash!, p1.BlockHash!), null);
        (await t1)?.Dispose();

        (ExecutionPayloadV4 p2, byte[][]? r2) = await BuildAmsterdamPayload(chain);
        await rpc.engine_newPayloadV5(p2, [], TestItem.KeccakE, r2 ?? []);
        await rpc.engine_forkchoiceUpdatedV4(new ForkchoiceStateV1(p2.BlockHash!, p2.BlockHash!, p2.BlockHash!), null);

        (ExecutionPayloadV4 p3, byte[][]? r3) = await BuildAmsterdamPayload(chain);
        Task<Witness?> t3 = proxy.RequestWitness(p3.BlockHash!);
        await rpc.engine_newPayloadV5(p3, [], TestItem.KeccakE, r3 ?? []);

        t3.IsCompletedSuccessfully.Should().BeTrue(
            "an armed capture for block 3 must succeed even after an unarmed block 2");
        using Witness? w3 = await t3;
        w3.Should().NotBeNull("block 3 must produce a valid witness");
    }

    [Test]
    [Category("WitnessCapture")]
    public async Task Handler_returns_witness_from_proxy_on_valid_status()
    {
        using Witness expectedWitness = MakeStubWitness();

        NewPayloadWithWitnessHandler handler = new WitnessHandlerBuilder
        {
            Proxy = MakeMockProxyReturning(expectedWitness),
            EngineModule = WitnessHandlerBuilder.SucceedingEngineModule(
                new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = TestItem.KeccakA }),
        }.Build();

        ResultWrapper<NewPayloadWithWitnessV1Result> result =
            await handler.HandleAsync(new ExecutionPayloadV4 { BlockHash = TestItem.KeccakA }, [], TestItem.KeccakA, []);

        result.Result.ResultType.Should().Be(ResultType.Success);
        result.Data.Status.Should().Be(PayloadStatus.Valid);
        result.Data.ExecutionWitness.Should().BeSameAs(expectedWitness);
    }

    [Test]
    [Category("WitnessCapture")]
    public async Task Handler_valid_status_with_null_witness_from_proxy_yields_null_witness()
    {
        NewPayloadWithWitnessHandler handler = new WitnessHandlerBuilder
        {
            Proxy = MakeMockProxyReturning(null),
            EngineModule = WitnessHandlerBuilder.SucceedingEngineModule(
                new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = TestItem.KeccakB }),
        }.Build();

        ResultWrapper<NewPayloadWithWitnessV1Result> result =
            await handler.HandleAsync(new ExecutionPayloadV4 { BlockHash = TestItem.KeccakA }, [], TestItem.KeccakA, []);

        result.Result.ResultType.Should().Be(ResultType.Success);
        result.Data.Status.Should().Be(PayloadStatus.Valid);
        result.Data.ExecutionWitness.Should().BeNull();
    }

    private static IEnumerable<TestCaseData> NonValidOutcomes()
    {
        yield return new TestCaseData((Func<IEngineRpcModule>)(() => WitnessHandlerBuilder.SucceedingEngineModule(
            new PayloadStatusV1 { Status = PayloadStatus.Syncing })))
            .SetName("SYNCING status");
        yield return new TestCaseData((Func<IEngineRpcModule>)(() => WitnessHandlerBuilder.SucceedingEngineModule(
            new PayloadStatusV1 { Status = PayloadStatus.Invalid, LatestValidHash = TestItem.KeccakD, ValidationError = "bad block" })))
            .SetName("INVALID status");
        yield return new TestCaseData((Func<IEngineRpcModule>)(() => WitnessHandlerBuilder.FailingEngineModule(
            "Unsupported fork", MergeErrorCodes.UnsupportedFork)))
            .SetName("RPC failure");
    }

    [TestCaseSource(nameof(NonValidOutcomes))]
    [Category("WitnessCapture")]
    public async Task Handler_calls_CancelWitnessRequest_when_not_valid(Func<IEngineRpcModule> moduleFactory)
    {
        WitnessCapturingWorldStateProxy proxy = MakeMockProxy();
        proxy.RequestWitness(Arg.Any<Hash256>())
            .Returns(new TaskCompletionSource<Witness?>().Task);

        NewPayloadWithWitnessHandler handler = new(new Lazy<IEngineRpcModule>(moduleFactory), new WitnessProxyResolver(proxy));

        await handler.HandleAsync(new ExecutionPayloadV4 { BlockHash = TestItem.KeccakA }, [], TestItem.KeccakA, []);

        proxy.Received(1).CancelWitnessRequest(Arg.Any<Hash256>());
    }

    [Test]
    [Category("WitnessCapture")]
    public async Task Handler_rejects_null_blockHash_with_InvalidParams_and_does_not_arm()
    {
        WitnessCapturingWorldStateProxy proxy = MakeMockProxy();

        NewPayloadWithWitnessHandler handler = new(
            new Lazy<IEngineRpcModule>(() => WitnessHandlerBuilder.SucceedingEngineModule(
                new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = TestItem.KeccakA })),
            new WitnessProxyResolver(proxy));

        ExecutionPayloadV4 payload = new()
        {
            BlockHash = null!
        };
        ResultWrapper<NewPayloadWithWitnessV1Result> result =
            await handler.HandleAsync(payload, [], TestItem.KeccakA, []);

        _ = proxy.DidNotReceive().RequestWitness(Arg.Any<Hash256>());
        result.Result.ResultType.Should().Be(ResultType.Failure,
            "a null blockHash is a malformed payload — return InvalidParams instead of forwarding");
        result.ErrorCode.Should().Be(ErrorCodes.InvalidParams);
    }

    [Test]
    [Category("WitnessCapture")]
    public async Task E2E_empty_Amsterdam_block_produces_VALID_with_non_null_witness()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);

        (ExecutionPayloadV4 payload, byte[][]? requests) = await BuildAmsterdamPayload(chain);

        ResultWrapper<NewPayloadWithWitnessV1Result> result =
            await chain.EngineRpcModule.engine_newPayloadWithWitness(
                payload, [], TestItem.KeccakE, requests ?? []);

        result.Result.ResultType.Should().Be(ResultType.Success);
        result.Data.Status.Should().Be(PayloadStatus.Valid);

        using Witness? witness = result.Data.ExecutionWitness;
        witness.Should().NotBeNull("VALID block must include a witness");
        witness!.State.Count.Should().BeGreaterThan(0,
            "witness State must contain at least the state root proof node");
    }

    [Test]
    [Category("WitnessCapture")]
    public async Task E2E_block_with_ETH_transfer_produces_multi_node_witness()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);

        Transaction tx = Build.A.Transaction
            .WithValue(UInt256.One)
            .WithTo(TestItem.AddressB)
            .WithMaxFeePerGas(20.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .WithType(TxType.EIP1559)
            .SignedAndResolved(chain.EthereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;
        chain.AddTransactions(tx);

        (ExecutionPayloadV4 payload, byte[][]? requests) = await BuildAmsterdamPayload(chain);

        ResultWrapper<NewPayloadWithWitnessV1Result> result =
            await chain.EngineRpcModule.engine_newPayloadWithWitness(
                payload, [], TestItem.KeccakE, requests ?? []);

        result.Data.Status.Should().Be(PayloadStatus.Valid);
        using Witness? witness = result.Data.ExecutionWitness;
        witness.Should().NotBeNull();
        witness!.State.Count.Should().BeGreaterThan(1,
            "a transfer touches sender, recipient and fee-recipient: at least 2 proof paths");
    }

    [Test]
    [Category("WitnessCapture")]
    public async Task E2E_witness_state_nodes_satisfy_spec_size_constraints()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);

        (ExecutionPayloadV4 payload, byte[][]? requests) = await BuildAmsterdamPayload(chain);

        ResultWrapper<NewPayloadWithWitnessV1Result> result =
            await chain.EngineRpcModule.engine_newPayloadWithWitness(
                payload, [], TestItem.KeccakE, requests ?? []);

        using Witness? witness = result.Data.ExecutionWitness;
        witness.Should().NotBeNull();

        foreach (byte[] node in witness!.State)
        {
            node.Should().NotBeEmpty("every state node must be a non-empty RLP blob");
            node.Length.Should().BeLessOrEqualTo(1_048_576,
                "each state element must not exceed MAX_WITNESS_ITEM_BYTES");
        }

        witness.State.Count.Should().BeLessOrEqualTo(1_048_576,
            "State list must not exceed MAX_WITNESS_ITEMS");
    }

    [Test]
    [Category("WitnessCapture")]
    public async Task E2E_sequential_blocks_produce_independent_witness_instances()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;

        (ExecutionPayloadV4 p1, byte[][]? r1) = await BuildAmsterdamPayload(chain);
        ResultWrapper<NewPayloadWithWitnessV1Result> res1 =
            await rpc.engine_newPayloadWithWitness(p1, [], TestItem.KeccakE, r1 ?? []);
        await rpc.engine_forkchoiceUpdatedV4(
            new ForkchoiceStateV1(p1.BlockHash!, p1.BlockHash!, p1.BlockHash!), null);

        (ExecutionPayloadV4 p2, byte[][]? r2) = await BuildAmsterdamPayload(chain);
        ResultWrapper<NewPayloadWithWitnessV1Result> res2 =
            await rpc.engine_newPayloadWithWitness(p2, [], TestItem.KeccakE, r2 ?? []);

        res1.Data.Status.Should().Be(PayloadStatus.Valid);
        res2.Data.Status.Should().Be(PayloadStatus.Valid);

        using Witness? w1 = res1.Data.ExecutionWitness;
        using Witness? w2 = res2.Data.ExecutionWitness;

        w1.Should().NotBeNull();
        w2.Should().NotBeNull();
        w1.Should().NotBeSameAs(w2,
            "each block produces its own Witness instance; shared reference indicates a tracking bug");
    }

    [Test]
    [Category("WitnessCapture")]
    public async Task E2E_non_VALID_response_has_null_witness_and_no_registry_leak()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);
        WitnessCapturingWorldStateProxy proxy = (WitnessCapturingWorldStateProxy)chain.MainWorldState;

        (ExecutionPayloadV4 good, byte[][]? requests) = await BuildAmsterdamPayload(chain);
        ExecutionPayloadV4 bad = new()
        {
            BlockHash = Keccak.Zero,
            ParentHash = good.ParentHash,
            FeeRecipient = good.FeeRecipient,
            StateRoot = good.StateRoot,
            ReceiptsRoot = good.ReceiptsRoot,
            LogsBloom = good.LogsBloom,
            PrevRandao = good.PrevRandao,
            BlockNumber = good.BlockNumber,
            GasLimit = good.GasLimit,
            GasUsed = good.GasUsed,
            Timestamp = good.Timestamp,
            ExtraData = good.ExtraData,
            BaseFeePerGas = good.BaseFeePerGas,
            Transactions = good.Transactions,
            Withdrawals = good.Withdrawals,
            BlobGasUsed = good.BlobGasUsed,
            ExcessBlobGas = good.ExcessBlobGas,
            ParentBeaconBlockRoot = good.ParentBeaconBlockRoot,
            ExecutionRequests = good.ExecutionRequests,
            BlockAccessList = good.BlockAccessList,
            SlotNumber = good.SlotNumber,
        };

        ResultWrapper<NewPayloadWithWitnessV1Result> result =
            await chain.EngineRpcModule.engine_newPayloadWithWitness(bad, [], TestItem.KeccakE, requests ?? []);

        result.Result.ResultType.Should().Be(ResultType.Success,
            "non-VALID status must still yield HTTP 200 / RPC success per the spec");
        result.Data.Status.Should().NotBe(PayloadStatus.Valid);
        result.Data.ExecutionWitness.Should().BeNull(
            "spec: witness must be None when status is not VALID");

        proxy.HasPendingRequest(Keccak.Zero).Should().BeFalse(
            "DisarmCapture must be called on non-VALID paths, leaving no orphaned TCS in the registry");
    }

    [Test]
    [Category("WitnessCapture")]
    public async Task Regression_ProcessOne_called_exactly_once_during_engine_newPayloadWithWitness()
    {
        int processCount = 0;

        using MergeTestBlockchain chain = await CreateBlockchain(
            Amsterdam.Instance,
            configurer: builder =>
                builder.AddDecorator<IBranchProcessor>((_, inner) =>
                    new CountingBranchProcessorDecorator(inner, () => Interlocked.Increment(ref processCount))));

        (ExecutionPayloadV4 payload, byte[][]? requests) = await BuildAmsterdamPayload(chain);
        processCount = 0;

        ResultWrapper<NewPayloadWithWitnessV1Result> result =
            await chain.EngineRpcModule.engine_newPayloadWithWitness(
                payload, [], TestItem.KeccakE, requests ?? []);

        result.Data.Status.Should().Be(PayloadStatus.Valid);
        result.Data.ExecutionWitness.Should().NotBeNull();

        processCount.Should().Be(1,
            "Option A must execute the block exactly once; " +
            "a count of 2 means the old double-execution bug has regressed");
    }

    [Test]
    [Category("WitnessCapture")]
    public async Task Regression_plain_engine_newPayloadV5_unaffected_by_witness_infrastructure()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);
        WitnessCapturingWorldStateProxy proxy = (WitnessCapturingWorldStateProxy)chain.MainWorldState;

        (ExecutionPayloadV4 payload, byte[][]? requests) = await BuildAmsterdamPayload(chain);
        Hash256 hash = payload.BlockHash!;

        ResultWrapper<PayloadStatusV1> result =
            await chain.EngineRpcModule.engine_newPayloadV5(payload, [], TestItem.KeccakE, requests ?? []);

        result.Data.Status.Should().Be(PayloadStatus.Valid,
            "the witness infrastructure must be completely transparent to the normal path");
        proxy.HasPendingRequest(hash).Should().BeFalse(
            "no registry entry should exist for a plain engine_newPayloadV5 call");
    }

    [Test]
    [Category("WitnessCapture")]
    public async Task Witness_state_nodes_are_consistent_with_parent_state_root()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);

        Transaction tx = Build.A.Transaction
            .WithValue(UInt256.One)
            .WithTo(TestItem.AddressB)
            .WithMaxFeePerGas(20.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .WithType(TxType.EIP1559)
            .SignedAndResolved(chain.EthereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;
        chain.AddTransactions(tx);

        (ExecutionPayloadV4 payload, byte[][]? requests) = await BuildAmsterdamPayload(chain);
        ResultWrapper<NewPayloadWithWitnessV1Result> result =
            await chain.EngineRpcModule.engine_newPayloadWithWitness(
                payload, [], TestItem.KeccakE, requests ?? []);

        result.Data.Status.Should().Be(PayloadStatus.Valid);
        using Witness? witness = result.Data.ExecutionWitness;
        witness.Should().NotBeNull();

        foreach (byte[] node in witness!.State)
        {
            node.Length.Should().BeGreaterThanOrEqualTo(1,
                "an empty node indicates drain ran before CommitTree populated the trie cache (Bug E2)");
        }
    }

    [Test]
    [Category("WitnessCapture")]
    public async Task Witness_headers_contain_at_least_parent_header()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);

        (ExecutionPayloadV4 payload, byte[][]? requests) = await BuildAmsterdamPayload(chain);
        ResultWrapper<NewPayloadWithWitnessV1Result> result =
            await chain.EngineRpcModule.engine_newPayloadWithWitness(
                payload, [], TestItem.KeccakE, requests ?? []);

        result.Data.Status.Should().Be(PayloadStatus.Valid);
        using Witness? witness = result.Data.ExecutionWitness;
        witness.Should().NotBeNull();

        witness!.Headers.Count.Should().BeGreaterThanOrEqualTo(1,
            "Witness.Headers must contain at least the parent block header " +
            "(WitnessGeneratingHeaderFinder.GetWitnessHeaders always includes parentHash). " +
            "A count of 0 indicates Bug E3 is not fixed: IHeaderFinder is not wired into WitnessCaptureRegistry.");
    }

    [Test]
    [Category("WitnessCapture")]
    public async Task Witness_headers_items_are_valid_RLP_encoded_block_headers()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);

        (ExecutionPayloadV4 payload, byte[][]? requests) = await BuildAmsterdamPayload(chain);
        ResultWrapper<NewPayloadWithWitnessV1Result> result =
            await chain.EngineRpcModule.engine_newPayloadWithWitness(
                payload, [], TestItem.KeccakE, requests ?? []);

        using Witness? witness = result.Data.ExecutionWitness;
        witness.Should().NotBeNull();

        foreach (byte[] header in witness!.Headers)
        {
            header.Should().NotBeEmpty("each header entry must be an RLP-encoded block header");
            header.Length.Should().BeLessOrEqualTo(1_048_576,
                "each header must fit within MAX_WITNESS_ITEM_BYTES per execution-apis#773");
        }
    }

    private static async Task<(ExecutionPayloadV4 Payload, byte[][]? ExecutionRequests)>
        BuildAmsterdamPayload(MergeTestBlockchain chain)
    {
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Block head = chain.BlockTree.Head!;

        PayloadAttributes attributes = new()
        {
            Timestamp = head.Timestamp + 1,
            PrevRandao = TestItem.KeccakH,
            SuggestedFeeRecipient = TestItem.AddressF,
            Withdrawals = [],
            ParentBeaconBlockRoot = TestItem.KeccakE,
            SlotNumber = (ulong?)(head.Number + 1),
        };

        Hash256 headHash = head.Hash!;
        ForkchoiceStateV1 fcu = new(headHash, headHash, headHash);

        Task improvementWait = chain.WaitForImprovedBlock(headHash);
        ResultWrapper<ForkchoiceUpdatedV1Result> fcuResult =
            await rpc.engine_forkchoiceUpdatedV4(fcu, attributes);
        fcuResult.Result.ResultType.Should().Be(ResultType.Success);

        await improvementWait;

        byte[] payloadIdBytes = Nethermind.Core.Extensions.Bytes.FromHexString(fcuResult.Data.PayloadId!);
        ResultWrapper<GetPayloadV6Result?> getPayload = await rpc.engine_getPayloadV6(payloadIdBytes);
        getPayload.Data.Should().NotBeNull();

        return (getPayload.Data!.ExecutionPayload, getPayload.Data!.ExecutionRequests);
    }

    private static WitnessCapturingWorldStateProxy MakeUnarmedProxy(
        IStateReader? stateReader = null,
        IHeaderFinder? headerFinder = null)
    {
        IWorldState inner = Substitute.For<IWorldState>();
        inner.TryGetAccount(Arg.Any<Address>(), out Arg.Any<AccountStruct>()).Returns(false);
        inner.StateRoot.Returns(Keccak.EmptyTreeHash);

        IHeaderFinder finder = headerFinder ?? Substitute.For<IHeaderFinder>();
        finder.Get(Arg.Any<Hash256>(), Arg.Any<long?>()).Returns(Build.A.BlockHeader.TestObject);

        return new WitnessCapturingWorldStateProxy(
            inner,
            stateReader ?? Substitute.For<IStateReader>(),
            finder,
            LimboLogs.Instance);
    }

    private static WitnessCapturingWorldStateProxy MakeMockProxy()
    {
        IWorldState inner = Substitute.For<IWorldState>();
        inner.StateRoot.Returns(Keccak.EmptyTreeHash);
        return Substitute.For<WitnessCapturingWorldStateProxy>(
            inner,
            Substitute.For<IStateReader>(),
            Substitute.For<IHeaderFinder>(),
            LimboLogs.Instance);
    }

    private static WitnessCapturingWorldStateProxy MakeMockProxyReturning(Witness? witness)
    {
        WitnessCapturingWorldStateProxy proxy = MakeMockProxy();
        proxy.RequestWitness(Arg.Any<Hash256>()).Returns(Task.FromResult(witness));
        return proxy;
    }

    private sealed class CountingBranchProcessorDecorator(IBranchProcessor inner, Action onProcess)
        : IBranchProcessor
    {
        public event EventHandler<BlockProcessedEventArgs>? BlockProcessed
        {
            add => inner.BlockProcessed += value;
            remove => inner.BlockProcessed -= value;
        }

        public event EventHandler<BlocksProcessingEventArgs>? BlocksProcessing
        {
            add => inner.BlocksProcessing += value;
            remove => inner.BlocksProcessing -= value;
        }

        public event EventHandler<BlockEventArgs>? BlockProcessing
        {
            add => inner.BlockProcessing += value;
            remove => inner.BlockProcessing -= value;
        }

        public Block[] Process(
            BlockHeader? baseBlock,
            IReadOnlyList<Block> suggestedBlocks,
            ProcessingOptions processingOptions,
            IBlockTracer blockTracer,
            CancellationToken token = default)
        {
            onProcess();
            return inner.Process(baseBlock, suggestedBlocks, processingOptions, blockTracer, token);
        }
    }
}
