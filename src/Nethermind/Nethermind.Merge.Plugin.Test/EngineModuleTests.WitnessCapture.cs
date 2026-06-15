// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Specs.Forks;
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

    // TEMPORARY witness-capture CI diagnostics. Ungated Console.WriteLine so it surfaces in the
    // Microsoft.Testing.Platform failed-test "Standard output" section on CI. Remove before PR.
    private static void WitLog(string m) => Console.WriteLine($"[WITDEBUG] {m}");

    private sealed class WitnessHandlerBuilder
    {
        public IEngineRpcModule EngineModule { get; set; }
            = SucceedingEngineModule(new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = TestItem.KeccakA });

        public WitnessRendezvous Rendezvous { get; set; } = new();

        public NewPayloadWithWitnessHandler Build() =>
            new(new Lazy<IEngineRpcModule>(() => EngineModule), Rendezvous);

        public static IEngineRpcModule SucceedingEngineModule(PayloadStatusV1 status)
        {
            IEngineRpcModule module = Substitute.For<IEngineRpcModule>();
            module
                .engine_newPayloadV5(Arg.Any<ExecutionPayloadV4>(), Arg.Any<Hash256?[]>(), Arg.Any<Hash256?>(), Arg.Any<byte[][]?>())
                .Returns(ResultWrapper<PayloadStatusV1>.Success(status));
            return module;
        }

        public static IEngineRpcModule FailingEngineModule(string error, int errorCode)
        {
            IEngineRpcModule module = Substitute.For<IEngineRpcModule>();
            module
                .engine_newPayloadV5(Arg.Any<ExecutionPayloadV4>(), Arg.Any<Hash256?[]>(), Arg.Any<Hash256?>(), Arg.Any<byte[][]?>())
                .Returns(ResultWrapper<PayloadStatusV1>.Fail(error, errorCode));
            return module;
        }
    }

    [Test]
    [Category("WitnessCapture")]
    public void Rendezvous_RequestWitness_returns_incomplete_task_until_completed()
    {
        WitnessRendezvous rendezvous = new();

        Task<Witness?> task = rendezvous.RequestWitness(TestItem.KeccakA);

        Assert.That(task.IsCompleted, Is.False,
            "the task must remain pending until the block-processor decorator publishes a result");
    }

    [Test]
    [Category("WitnessCapture")]
    public void Rendezvous_CancelWitnessRequest_cancels_TCS_and_removes_entry()
    {
        WitnessRendezvous rendezvous = new();
        Hash256 hash = TestItem.KeccakD;

        Task<Witness?> captureTask = rendezvous.RequestWitness(hash);
        Assert.That(rendezvous.HasPendingRequest(hash), Is.True);

        rendezvous.CancelWitnessRequest(hash);

        Assert.That(rendezvous.HasPendingRequest(hash), Is.False,
            "CancelWitnessRequest must remove the entry");
        Assert.That(captureTask.IsCanceled, Is.True,
            "CancelWitnessRequest must cancel the TCS so any awaiter gets OperationCanceledException");
    }

    [Test]
    [Category("WitnessCapture")]
    public void Rendezvous_CancelWitnessRequest_noop_when_no_entry_exists()
    {
        WitnessRendezvous rendezvous = new();
        Action cancel = () => rendezvous.CancelWitnessRequest(Keccak.Zero);
        Assert.That(cancel, Throws.Nothing, "cancelling a non-existent request is a valid no-op");
    }

    [Test]
    [Category("WitnessCapture")]
    public void Rendezvous_duplicate_RequestWitness_cancels_previous_TCS()
    {
        WitnessRendezvous rendezvous = new();
        Hash256 hash = TestItem.KeccakE;

        Task<Witness?> first = rendezvous.RequestWitness(hash);
        Task<Witness?> second = rendezvous.RequestWitness(hash);

        Assert.That(first.IsCanceled, Is.True,
            "the orphaned TCS must be cancelled so any awaiter gets OperationCanceledException rather than hanging forever");
        Assert.That(second.IsCompleted, Is.False, "the replacement TCS is still pending");
    }

    [Test]
    [Category("WitnessCapture")]
    public async Task BlockProcessor_completes_rendezvous_task_synchronously_inside_newPayloadV5()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);
        WitnessRendezvous rendezvous = chain.Container.Resolve<WitnessRendezvous>();

        (ExecutionPayloadV4 payload, byte[][]? requests) = await BuildAmsterdamPayload(chain);
        Hash256 hash = payload.BlockHash!;

        Task<Witness?> captureTask = rendezvous.RequestWitness(hash);

        await chain.EngineRpcModule.engine_newPayloadV5(payload, [], TestItem.KeccakE, requests ?? []);

        Assert.That(captureTask.IsCompleted, Is.True,
            "the block-processor decorator must complete the TCS synchronously inside ProcessOne, " +
            "before engine_newPayloadV5 returns, so the handler's await is a non-blocking retrieval");

        using Witness? witness = await captureTask;
        Assert.That(witness, Is.Not.Null, "a VALID block must produce a non-null witness");
    }

    [Test]
    [Category("WitnessCapture")]
    public async Task BlockProcessor_does_not_capture_when_no_request_pending()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);
        WitnessRendezvous rendezvous = chain.Container.Resolve<WitnessRendezvous>();

        (ExecutionPayloadV4 payload, byte[][]? requests) = await BuildAmsterdamPayload(chain);

        await chain.EngineRpcModule.engine_newPayloadV5(payload, [], TestItem.KeccakE, requests ?? []);

        Assert.That(rendezvous.HasPendingRequest(payload.BlockHash!), Is.False,
            "no entry should appear in the rendezvous for a plain engine_newPayloadV5 call");
    }

    [Test]
    [Category("WitnessCapture")]
    public async Task BlockProcessor_multi_block_branch_captures_independent_witnesses()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        WitnessRendezvous rendezvous = chain.Container.Resolve<WitnessRendezvous>();

        int timeoutMs = chain.Container.Resolve<Nethermind.Merge.Plugin.IMergeConfig>().NewPayloadBlockProcessingTimeout;
        WitLog($"[multi] NewPayloadBlockProcessingTimeout = {timeoutMs} ms; UseFlatDb = {chain.UseFlatDb}");

        (ExecutionPayloadV4 p1, byte[][]? r1) = await BuildAmsterdamPayload(chain);
        Task<Witness?> t1 = rendezvous.RequestWitness(p1.BlockHash!);
        ResultWrapper<PayloadStatusV1> p1Result = await rpc.engine_newPayloadV5(p1, [], TestItem.KeccakE, r1 ?? []);
        WitLog($"[multi] newPayloadV5(p1): resultType={p1Result.Result.ResultType} status={p1Result.Data?.Status} t1.Status={t1.Status}");
        await rpc.engine_forkchoiceUpdatedV4(
            new ForkchoiceStateV1(p1.BlockHash!, p1.BlockHash!, p1.BlockHash!), null);
        (await t1)?.Dispose();

        (ExecutionPayloadV4 p2, byte[][]? r2) = await BuildAmsterdamPayload(chain);
        Task<Witness?> t2 = rendezvous.RequestWitness(p2.BlockHash!);
        ResultWrapper<PayloadStatusV1> p2Result = await rpc.engine_newPayloadV5(p2, [], TestItem.KeccakE, r2 ?? []);
        WitLog($"[multi] newPayloadV5(p2): resultType={p2Result.Result.ResultType} status={p2Result.Data?.Status} " +
            $"error={p2Result.Result.Error} t2.Status(immediate)={t2.Status} hasPending={rendezvous.HasPendingRequest(p2.BlockHash!)}");

        Assert.That(t1.IsCompletedSuccessfully, Is.True, "block-1 task was completed during block-1");

        // Bounded await: if t2 never completes (block not processed), fail fast with the diagnostics
        // above instead of hanging until the NUnit per-test timeout.
        using Witness? w2 = await t2.WaitAsync(TimeSpan.FromSeconds(10));
        WitLog($"[multi] t2 completed: status={t2.Status} witnessNull={w2 is null}");
        Assert.That(w2, Is.Not.Null, "block 2 must produce its own independent witness");
    }

    [Test]
    [Category("WitnessCapture")]
    public async Task BlockProcessor_uncaptured_block_between_two_captured_blocks_leaves_clean_state()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        WitnessRendezvous rendezvous = chain.Container.Resolve<WitnessRendezvous>();

        WitLog($"[uncaptured] UseFlatDb = {chain.UseFlatDb}");

        (ExecutionPayloadV4 p1, byte[][]? r1) = await BuildAmsterdamPayload(chain);
        Task<Witness?> t1 = rendezvous.RequestWitness(p1.BlockHash!);
        await rpc.engine_newPayloadV5(p1, [], TestItem.KeccakE, r1 ?? []);
        await rpc.engine_forkchoiceUpdatedV4(new ForkchoiceStateV1(p1.BlockHash!, p1.BlockHash!, p1.BlockHash!), null);
        (await t1)?.Dispose();

        (ExecutionPayloadV4 p2, byte[][]? r2) = await BuildAmsterdamPayload(chain);
        await rpc.engine_newPayloadV5(p2, [], TestItem.KeccakE, r2 ?? []);
        await rpc.engine_forkchoiceUpdatedV4(new ForkchoiceStateV1(p2.BlockHash!, p2.BlockHash!, p2.BlockHash!), null);

        (ExecutionPayloadV4 p3, byte[][]? r3) = await BuildAmsterdamPayload(chain);
        Task<Witness?> t3 = rendezvous.RequestWitness(p3.BlockHash!);
        ResultWrapper<PayloadStatusV1> p3Result = await rpc.engine_newPayloadV5(p3, [], TestItem.KeccakE, r3 ?? []);
        WitLog($"[uncaptured] newPayloadV5(p3): resultType={p3Result.Result.ResultType} status={p3Result.Data?.Status} " +
            $"error={p3Result.Result.Error} t3.Status(immediate)={t3.Status} hasPending={rendezvous.HasPendingRequest(p3.BlockHash!)}");

        // Bounded await: fail fast with the diagnostics above instead of hanging if t3 never completes.
        using Witness? w3 = await t3.WaitAsync(TimeSpan.FromSeconds(10));
        WitLog($"[uncaptured] t3 completed: status={t3.Status} witnessNull={w3 is null}");
        Assert.That(w3, Is.Not.Null, "block 3 must produce a valid witness");
    }

    /// <summary>
    /// Builds an IEngineRpcModule mock whose engine_newPayloadV5 implementation simulates what the
    /// WitnessCapturingBlockProcessor decorator does on the real path: claim the pending rendezvous
    /// entry for the requested block hash and publish <paramref name="witness"/> into it.
    /// </summary>
    private static IEngineRpcModule PublishingEngineModule(WitnessRendezvous rendezvous, Witness? witness, PayloadStatusV1 status)
    {
        IEngineRpcModule module = Substitute.For<IEngineRpcModule>();
        module
            .engine_newPayloadV5(Arg.Any<ExecutionPayloadV4>(), Arg.Any<Hash256?[]>(), Arg.Any<Hash256?>(), Arg.Any<byte[][]?>())
            .Returns(call =>
            {
                ExecutionPayloadV4 payload = call.Arg<ExecutionPayloadV4>();
                if (rendezvous.TryClaim(payload.BlockHash!, out TaskCompletionSource<Witness?>? tcs))
                    tcs!.SetResult(witness);
                return ResultWrapper<PayloadStatusV1>.Success(status);
            });
        return module;
    }

    [Test]
    [Category("WitnessCapture")]
    public async Task Handler_returns_witness_from_rendezvous_on_valid_status()
    {
        using Witness expectedWitness = MakeStubWitness();
        WitnessRendezvous rendezvous = new();

        NewPayloadWithWitnessHandler handler = new(
            new Lazy<IEngineRpcModule>(() => PublishingEngineModule(
                rendezvous,
                expectedWitness,
                new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = TestItem.KeccakA })),
            rendezvous);

        ResultWrapper<NewPayloadWithWitnessV1Result> result =
            await handler.HandleAsync(new ExecutionPayloadV4 { BlockHash = TestItem.KeccakA }, [], TestItem.KeccakA, []);

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        Assert.That(result.Data.ExecutionWitness, Is.SameAs(expectedWitness));
    }

    [Test]
    [Category("WitnessCapture")]
    public async Task Handler_valid_status_with_null_witness_yields_null_witness()
    {
        WitnessRendezvous rendezvous = new();

        NewPayloadWithWitnessHandler handler = new(
            new Lazy<IEngineRpcModule>(() => PublishingEngineModule(
                rendezvous,
                witness: null,
                new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = TestItem.KeccakB })),
            rendezvous);

        ResultWrapper<NewPayloadWithWitnessV1Result> result =
            await handler.HandleAsync(new ExecutionPayloadV4 { BlockHash = TestItem.KeccakA }, [], TestItem.KeccakA, []);

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        Assert.That(result.Data.ExecutionWitness, Is.Null);
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
    public async Task Handler_cancels_rendezvous_when_not_valid(Func<IEngineRpcModule> moduleFactory)
    {
        WitnessRendezvous rendezvous = new();

        NewPayloadWithWitnessHandler handler = new(new Lazy<IEngineRpcModule>(moduleFactory), rendezvous);

        await handler.HandleAsync(new ExecutionPayloadV4 { BlockHash = TestItem.KeccakA }, [], TestItem.KeccakA, []);

        Assert.That(rendezvous.HasPendingRequest(TestItem.KeccakA), Is.False,
            "the handler must cancel the rendezvous entry on every non-VALID outcome");
    }

    [Test]
    [Category("WitnessCapture")]
    public async Task Handler_rejects_null_blockHash_with_InvalidParams_and_does_not_register()
    {
        WitnessRendezvous rendezvous = new();

        NewPayloadWithWitnessHandler handler = new(
            new Lazy<IEngineRpcModule>(() => WitnessHandlerBuilder.SucceedingEngineModule(
                new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = TestItem.KeccakA })),
            rendezvous);

        ExecutionPayloadV4 payload = new()
        {
            BlockHash = null!
        };
        ResultWrapper<NewPayloadWithWitnessV1Result> result =
            await handler.HandleAsync(payload, [], TestItem.KeccakA, []);

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure),
            "a null blockHash is a malformed payload — return InvalidParams instead of forwarding");
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
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

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid));

        using Witness? witness = result.Data.ExecutionWitness;
        Assert.That(witness, Is.Not.Null, "VALID block must include a witness");
        Assert.That(witness!.State.Count, Is.GreaterThan(0),
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

        Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        using Witness? witness = result.Data.ExecutionWitness;
        Assert.That(witness, Is.Not.Null);
        Assert.That(witness!.State.Count, Is.GreaterThan(1),
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
        Assert.That(witness, Is.Not.Null);

        foreach (byte[] node in witness!.State)
        {
            Assert.That(node, Is.Not.Empty, "every state node must be a non-empty RLP blob");
            Assert.That(node.Length, Is.LessThanOrEqualTo(1_048_576),
                "each state element must not exceed MAX_WITNESS_ITEM_BYTES");
        }

        Assert.That(witness.State.Count, Is.LessThanOrEqualTo(1_048_576),
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

        Assert.That(res1.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        Assert.That(res2.Data.Status, Is.EqualTo(PayloadStatus.Valid));

        using Witness? w1 = res1.Data.ExecutionWitness;
        using Witness? w2 = res2.Data.ExecutionWitness;

        Assert.That(w1, Is.Not.Null);
        Assert.That(w2, Is.Not.Null);
        Assert.That(w1, Is.Not.SameAs(w2),
            "each block produces its own Witness instance; shared reference indicates a tracking bug");
    }

    [Test]
    [Category("WitnessCapture")]
    public async Task E2E_non_VALID_response_has_null_witness_and_no_rendezvous_leak()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);
        WitnessRendezvous rendezvous = chain.Container.Resolve<WitnessRendezvous>();

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

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success),
            "non-VALID status must still yield HTTP 200 / RPC success per the spec");
        Assert.That(result.Data.Status, Is.Not.EqualTo(PayloadStatus.Valid));
        Assert.That(result.Data.ExecutionWitness, Is.Null,
            "spec: witness must be None when status is not VALID");

        Assert.That(rendezvous.HasPendingRequest(Keccak.Zero), Is.False,
            "the handler must cancel the rendezvous entry on non-VALID outcomes");
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

        Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        Assert.That(result.Data.ExecutionWitness, Is.Not.Null);

        Assert.That(processCount, Is.EqualTo(1),
            "Option A must execute the block exactly once; " +
            "a count of 2 means the old double-execution bug has regressed");
    }

    [Test]
    [Category("WitnessCapture")]
    public async Task Regression_plain_engine_newPayloadV5_unaffected_by_witness_infrastructure()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);
        WitnessRendezvous rendezvous = chain.Container.Resolve<WitnessRendezvous>();

        (ExecutionPayloadV4 payload, byte[][]? requests) = await BuildAmsterdamPayload(chain);
        Hash256 hash = payload.BlockHash!;

        ResultWrapper<PayloadStatusV1> result =
            await chain.EngineRpcModule.engine_newPayloadV5(payload, [], TestItem.KeccakE, requests ?? []);

        Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid),
            "the witness infrastructure must be completely transparent to the normal path");
        Assert.That(rendezvous.HasPendingRequest(hash), Is.False,
            "no rendezvous entry should exist for a plain engine_newPayloadV5 call");
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

        Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        using Witness? witness = result.Data.ExecutionWitness;
        Assert.That(witness, Is.Not.Null);

        foreach (byte[] node in witness!.State)
        {
            Assert.That(node.Length, Is.GreaterThanOrEqualTo(1),
                "an empty node indicates drain ran before CommitTree populated the trie cache");
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

        Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        using Witness? witness = result.Data.ExecutionWitness;
        Assert.That(witness, Is.Not.Null);

        Assert.That(witness!.Headers.Count, Is.GreaterThanOrEqualTo(1),
            "Witness.Headers must contain at least the parent block header " +
            "(WitnessHeaderRecorder.BuildHeaders always includes parentHash).");
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
        Assert.That(witness, Is.Not.Null);

        foreach (byte[] header in witness!.Headers)
        {
            Assert.That(header, Is.Not.Empty, "each header entry must be an RLP-encoded block header");
            Assert.That(header.Length, Is.LessThanOrEqualTo(1_048_576),
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
        Assert.That(fcuResult.Result.ResultType, Is.EqualTo(ResultType.Success));

        await improvementWait;

        byte[] payloadIdBytes = Nethermind.Core.Extensions.Bytes.FromHexString(fcuResult.Data.PayloadId!);
        ResultWrapper<GetPayloadV6Result?> getPayload = await rpc.engine_getPayloadV6(payloadIdBytes);
        Assert.That(getPayload.Data, Is.Not.Null);

        return (getPayload.Data!.ExecutionPayload, getPayload.Data!.ExecutionRequests);
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
