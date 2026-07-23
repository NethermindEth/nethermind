// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [Test]
    public async Task Should_process_block_as_expected_V7()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;

        PayloadAttributes payloadAttrs = BuildBogotaPayloadAttributes(inclusionList: []);
        ForkchoiceStateV1 fcuState = new(startingHead, Keccak.Zero, startingHead);

        ResultWrapper<ForkchoiceUpdatedV1Result> fcuResult = await rpc.engine_forkchoiceUpdatedV5(fcuState, payloadAttrs);
        Assert.That(fcuResult.Result.ResultType, Is.EqualTo(ResultType.Success), fcuResult.Result.Error);
        Assert.That(fcuResult.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
        Assert.That(fcuResult.Data.PayloadId, Is.Not.Null);

        ResultWrapper<GetPayloadV6Result?> payloadResult = await rpc.engine_getPayloadV6(Bytes.FromHexString(fcuResult.Data.PayloadId!));
        Assert.That(payloadResult.Data, Is.Not.Null);
        ExecutionPayloadV4 executionPayload = payloadResult.Data!.ExecutionPayload;
        Assert.That(executionPayload.Transactions, Is.Empty);

        ResultWrapper<PayloadStatusV2> newPayload = await rpc.engine_newPayloadV6(
            executionPayload,
            blobVersionedHashes: [],
            parentBeaconBlockRoot: Keccak.Zero,
            executionRequests: payloadResult.Data!.ExecutionRequests,
            inclusionListTransactions: []);
        Assert.That(newPayload.Result.ResultType, Is.EqualTo(ResultType.Success), newPayload.Result.Error);
        Assert.That(newPayload.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        Assert.That(newPayload.Data.InclusionListSatisfied, Is.True);
        Assert.That(newPayload.Data.LatestValidHash, Is.EqualTo(executionPayload.BlockHash));

        // Promote the new block to head, finalized, and safe.
        ResultWrapper<ForkchoiceUpdatedV1Result> finalFcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(executionPayload.BlockHash, executionPayload.BlockHash, executionPayload.BlockHash),
            payloadAttributes: null);
        Assert.That(finalFcu.Result.ResultType, Is.EqualTo(ResultType.Success), finalFcu.Result.Error);
        Assert.That(finalFcu.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
        Assert.That(finalFcu.Data.PayloadStatus.LatestValidHash, Is.EqualTo(executionPayload.BlockHash));
        Assert.That(finalFcu.Data.PayloadId, Is.Null);
    }

    [Test]
    public async Task NewPayloadV6_should_report_unsatisfied_inclusion_list()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;

        // Baseline empty payload — engine computes hashes so the test stays stable across
        // unrelated Amsterdam changes.
        ResultWrapper<ForkchoiceUpdatedV1Result> baselineFcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
            BuildBogotaPayloadAttributes(inclusionList: []));
        ResultWrapper<GetPayloadV6Result?> baselinePayload = await rpc.engine_getPayloadV6(Bytes.FromHexString(baselineFcu.Data.PayloadId!));
        ExecutionPayloadV4 emptyPayload = baselinePayload.Data!.ExecutionPayload;

        // Censored tx: a normal transfer that fits in the empty payload → IL unsatisfied.
        Transaction censoredTx = Build.A.Transaction
            .WithNonce(0)
            .WithMaxFeePerGas(10.GWei)
            .WithMaxPriorityFeePerGas(2.GWei)
            .WithGasLimit(100_000)
            .WithTo(TestItem.AddressA)
            .SignedAndResolved(TestItem.PrivateKeyB)
            .TestObject;
        byte[][] inclusionList = [Rlp.Encode(censoredTx).Bytes];

        ResultWrapper<PayloadStatusV2> result = await rpc.engine_newPayloadV6(
            emptyPayload,
            blobVersionedHashes: [],
            parentBeaconBlockRoot: Keccak.Zero,
            executionRequests: baselinePayload.Data!.ExecutionRequests,
            inclusionListTransactions: inclusionList);

        // execution-apis#609: a censoring payload stays VALID and reports inclusionListSatisfied=false.
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success), result.Result.Error);
        Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        Assert.That(result.Data.InclusionListSatisfied, Is.False);
        Assert.That(result.Data.LatestValidHash, Is.EqualTo(emptyPayload.BlockHash));
    }

    [Test]
    public async Task NewPayloadV6_should_revalidate_same_block_against_new_inclusion_list()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;

        ResultWrapper<ForkchoiceUpdatedV1Result> fcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
            BuildBogotaPayloadAttributes(inclusionList: []));
        ResultWrapper<GetPayloadV6Result?> payloadResult = await rpc.engine_getPayloadV6(Bytes.FromHexString(fcu.Data.PayloadId!));
        ExecutionPayloadV4 emptyPayload = payloadResult.Data!.ExecutionPayload;

        ResultWrapper<PayloadStatusV2> first = await rpc.engine_newPayloadV6(
            emptyPayload,
            blobVersionedHashes: [],
            parentBeaconBlockRoot: Keccak.Zero,
            executionRequests: payloadResult.Data!.ExecutionRequests,
            inclusionListTransactions: []);
        Assert.That(first.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        Assert.That(first.Data.InclusionListSatisfied, Is.True);

        // Same block hash, different IL: the cached VALID must not short-circuit the IL check.
        Transaction censoredTx = Build.A.Transaction
            .WithNonce(0)
            .WithMaxFeePerGas(10.GWei)
            .WithMaxPriorityFeePerGas(2.GWei)
            .WithGasLimit(100_000)
            .WithTo(TestItem.AddressA)
            .SignedAndResolved(TestItem.PrivateKeyB)
            .TestObject;
        ResultWrapper<PayloadStatusV2> second = await rpc.engine_newPayloadV6(
            emptyPayload,
            blobVersionedHashes: [],
            parentBeaconBlockRoot: Keccak.Zero,
            executionRequests: payloadResult.Data!.ExecutionRequests,
            inclusionListTransactions: [Rlp.Encode(censoredTx).Bytes]);
        Assert.That(second.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        Assert.That(second.Data.InclusionListSatisfied, Is.False);
    }

    [Test]
    public async Task NewPayloadV6_accepts_aggregate_inclusion_list_exceeding_single_member_cap()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;

        ResultWrapper<ForkchoiceUpdatedV1Result> fcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
            BuildBogotaPayloadAttributes(inclusionList: []));
        ResultWrapper<GetPayloadV6Result?> payloadResult = await rpc.engine_getPayloadV6(Bytes.FromHexString(fcu.Data.PayloadId!));

        // The flattened aggregate of up to 16 committee members can exceed the per-member 8 KiB cap;
        // newPayloadV6 must not reject it (two ~6 KiB member lists here total > MAX_BYTES_PER_INCLUSION_LIST).
        byte[] member = new byte[Eip7805Constants.MaxBytesPerInclusionList * 3 / 4];
        ResultWrapper<PayloadStatusV2> result = await rpc.engine_newPayloadV6(
            payloadResult.Data!.ExecutionPayload,
            blobVersionedHashes: [],
            parentBeaconBlockRoot: Keccak.Zero,
            executionRequests: payloadResult.Data!.ExecutionRequests,
            inclusionListTransactions: [member, member]);

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
    }

    [Test]
    public async Task NewPayloadV6_bounds_aggregate_inclusion_list_bytes()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;

        ResultWrapper<ForkchoiceUpdatedV1Result> fcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
            BuildBogotaPayloadAttributes(inclusionList: []));
        ResultWrapper<GetPayloadV6Result?> payloadResult = await rpc.engine_getPayloadV6(Bytes.FromHexString(fcu.Data.PayloadId!));
        ExecutionPayloadV4 emptyPayload = payloadResult.Data!.ExecutionPayload;

        // At the aggregate limit (IL_COMMITTEE_SIZE * MAX_BYTES_PER_INCLUSION_LIST): accepted.
        ResultWrapper<PayloadStatusV2> atLimit = await rpc.engine_newPayloadV6(
            emptyPayload, [], Keccak.Zero, payloadResult.Data!.ExecutionRequests,
            [new byte[Eip7805Constants.MaxAggregateInclusionListBytes]]);
        Assert.That(atLimit.Result.ResultType, Is.EqualTo(ResultType.Success), atLimit.Result.Error);

        // One byte over the limit: rejected before decode.
        ResultWrapper<PayloadStatusV2> overLimit = await rpc.engine_newPayloadV6(
            emptyPayload, [], Keccak.Zero, payloadResult.Data!.ExecutionRequests,
            [new byte[Eip7805Constants.MaxAggregateInclusionListBytes + 1]]);
        Assert.That(overLimit.Result.ResultType, Is.EqualTo(ResultType.Failure));
        Assert.That(overLimit.Result.Error, Does.Contain("exceeds the maximum aggregate size"));
    }

    [Test]
    public async Task NewPayloadV5_is_unsupported_at_Bogota()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;

        ResultWrapper<ForkchoiceUpdatedV1Result> fcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
            BuildBogotaPayloadAttributes(inclusionList: []));
        ResultWrapper<GetPayloadV6Result?> payloadResult = await rpc.engine_getPayloadV6(Bytes.FromHexString(fcu.Data.PayloadId!));

        // execution-apis#609: at/after Bogota, engine_newPayloadV5 must be rejected with -38005.
        ResultWrapper<PayloadStatusV1> result = await rpc.engine_newPayloadV5(
            payloadResult.Data!.ExecutionPayload, [], Keccak.Zero, payloadResult.Data!.ExecutionRequests);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
        Assert.That(result.ErrorCode, Is.EqualTo(MergeErrorCodes.UnsupportedFork));
    }

    [Test]
    public async Task NewPayloadV6_is_unsupported_before_Bogota()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Block genesis = chain.BlockFinder.FindGenesisBlock()!;

        PayloadAttributes attrs = new()
        {
            Timestamp = genesis.Header.Timestamp + 12,
            PrevRandao = genesis.Header.Random!,
            SuggestedFeeRecipient = TestItem.AddressC,
            Withdrawals = [],
            ParentBeaconBlockRoot = Keccak.Zero,
            SlotNumber = 1,
            TargetGasLimit = genesis.Header.GasLimit,
        };
        ForkchoiceStateV1 fcuState = new(genesis.Hash!, genesis.Hash!, genesis.Hash!);
        ResultWrapper<ForkchoiceUpdatedV1Result> fcu = await rpc.engine_forkchoiceUpdatedV4(fcuState, attrs);
        ResultWrapper<GetPayloadV6Result?> payloadResult = await rpc.engine_getPayloadV6(Bytes.FromHexString(fcu.Data.PayloadId!));

        // execution-apis#609: before Bogota, engine_newPayloadV6 must be rejected with -38005.
        ResultWrapper<PayloadStatusV2> result = await rpc.engine_newPayloadV6(
            payloadResult.Data!.ExecutionPayload, [], Keccak.Zero, payloadResult.Data!.ExecutionRequests, []);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
        Assert.That(result.ErrorCode, Is.EqualTo(MergeErrorCodes.UnsupportedFork));
    }

    [Test]
    public async Task ForkchoiceUpdatedV5_accepts_null_inclusion_list_for_initial_build()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;

        // The proposer's initial FCUv5 carries no inclusion list yet — it must still start building.
        ResultWrapper<ForkchoiceUpdatedV1Result> fcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
            BuildBogotaPayloadAttributes(inclusionList: null!));

        Assert.That(fcu.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.That(fcu.Data.PayloadId, Is.Not.Null);
    }

    [Test]
    public async Task Should_build_block_with_inclusion_list_transactions()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;

        Transaction tx = Build.A.Transaction
            .WithNonce(0)
            .WithMaxFeePerGas(10.GWei)
            .WithMaxPriorityFeePerGas(2.GWei)
            .WithTo(TestItem.AddressA)
            .SignedAndResolved(TestItem.PrivateKeyB)
            .TestObject;
        byte[] txBytes = Rlp.Encode(tx).Bytes;

        ResultWrapper<ForkchoiceUpdatedV1Result> fcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
            BuildBogotaPayloadAttributes(inclusionList: [txBytes]));

        Assert.That(fcu.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
        Assert.That(fcu.Data.PayloadId, Is.Not.Null);

        // With a non-empty IL the producer skips its EmptyBlock fast path, so the first
        // getPayload already returns a populated payload — no polling needed.
        ResultWrapper<GetPayloadV6Result?> payloadResult = await rpc.engine_getPayloadV6(Bytes.FromHexString(fcu.Data.PayloadId!));
        Assert.That(payloadResult.Data, Is.Not.Null);
        ExecutionPayloadV4 payload = payloadResult.Data!.ExecutionPayload;
        Assert.That(payload.Transactions, Has.Length.EqualTo(1));
        Assert.That(payload.Transactions[0], Is.EqualTo(txBytes));

        // The block-as-built must round-trip through newPayloadV6 with the same IL.
        ResultWrapper<PayloadStatusV2> verify = await rpc.engine_newPayloadV6(
            payload,
            blobVersionedHashes: [],
            parentBeaconBlockRoot: Keccak.Zero,
            executionRequests: [],
            inclusionListTransactions: [txBytes]);
        Assert.That(verify.Result.ResultType, Is.EqualTo(ResultType.Success), verify.Result.Error);
        Assert.That(verify.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        Assert.That(verify.Data.InclusionListSatisfied, Is.True);
    }

    [Test]
    public async Task Should_build_block_including_reversed_nonce_inclusion_list()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;

        Transaction tx0 = Build.A.Transaction
            .WithNonce(0).WithMaxFeePerGas(10.GWei).WithMaxPriorityFeePerGas(2.GWei)
            .WithTo(TestItem.AddressA).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Transaction tx1 = Build.A.Transaction
            .WithNonce(1).WithMaxFeePerGas(10.GWei).WithMaxPriorityFeePerGas(2.GWei)
            .WithTo(TestItem.AddressA).SignedAndResolved(TestItem.PrivateKeyB).TestObject;

        // Reversed order (nonce 1 before nonce 0): a one-pass producer would skip nonce 1 forever.
        byte[][] inclusionList = [Rlp.Encode(tx1).Bytes, Rlp.Encode(tx0).Bytes];

        ResultWrapper<ForkchoiceUpdatedV1Result> fcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
            BuildBogotaPayloadAttributes(inclusionList: inclusionList));
        ResultWrapper<GetPayloadV6Result?> payloadResult = await rpc.engine_getPayloadV6(Bytes.FromHexString(fcu.Data.PayloadId!));
        ExecutionPayloadV4 payload = payloadResult.Data!.ExecutionPayload;

        // Both IL txs must be produced, in ascending-nonce order.
        Assert.That(payload.Transactions, Has.Length.EqualTo(2));
        Assert.That(payload.Transactions[0], Is.EqualTo(Rlp.Encode(tx0).Bytes));
        Assert.That(payload.Transactions[1], Is.EqualTo(Rlp.Encode(tx1).Bytes));
    }

    [Test]
    public async Task Can_get_inclusion_list()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;

        Transaction tx1 = Build.A.Transaction
            .WithNonce(0)
            .WithMaxFeePerGas(10.GWei)
            .WithMaxPriorityFeePerGas(2.GWei)
            .WithTo(TestItem.AddressA)
            .SignedAndResolved(TestItem.PrivateKeyB)
            .TestObject;

        Transaction tx2 = Build.A.Transaction
            .WithNonce(1)
            .WithMaxFeePerGas(15.GWei)
            .WithMaxPriorityFeePerGas(3.GWei)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(TestItem.PrivateKeyB)
            .TestObject;

        chain.TxPool.SubmitTx(tx1, TxHandlingOptions.PersistentBroadcast);
        chain.TxPool.SubmitTx(tx2, TxHandlingOptions.PersistentBroadcast);

        using InclusionListBytes inclusionList = (await rpc.engine_getInclusionListV1()).Data!;

        byte[] tx1Bytes = Rlp.Encode(tx1).Bytes;
        byte[] tx2Bytes = Rlp.Encode(tx2).Bytes;
        byte[][] inclusionListBytes = inclusionList.Select(b => b.AsSpan().ToArray()).ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inclusionList, Is.Not.Null);
            Assert.That(inclusionList.Count, Is.EqualTo(2));
            Assert.That(inclusionListBytes, Does.Contain(tx1Bytes));
            Assert.That(inclusionListBytes, Does.Contain(tx2Bytes));
        }
    }

    private PayloadAttributes BuildBogotaPayloadAttributes(byte[][] inclusionList, ulong targetGasLimit = 30_000_000UL) => new()
    {
        Timestamp = Timestamper.UnixTime.Seconds,
        PrevRandao = Keccak.Zero,
        SuggestedFeeRecipient = TestItem.AddressC,
        Withdrawals = [],
        ParentBeaconBlockRoot = Keccak.Zero,
        SlotNumber = 1,
        // V4 attributes require TargetGasLimit (added by upstream after this test was written).
        TargetGasLimit = targetGasLimit,
        InclusionListTransactions = inclusionList,
    };
}
