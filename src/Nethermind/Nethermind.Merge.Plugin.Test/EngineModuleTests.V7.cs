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

        ResultWrapper<PayloadStatusV1> newPayload = await rpc.engine_newPayloadV6(
            executionPayload,
            blobVersionedHashes: [],
            parentBeaconBlockRoot: Keccak.Zero,
            executionRequests: payloadResult.Data!.ExecutionRequests,
            inclusionListTransactions: []);
        Assert.That(newPayload.Result.ResultType, Is.EqualTo(ResultType.Success), newPayload.Result.Error);
        Assert.That(newPayload.Data.Status, Is.EqualTo(PayloadStatus.Valid));
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
    public async Task NewPayloadV6_should_return_invalid_for_unsatisfied_inclusion_list()
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

        ResultWrapper<PayloadStatusV1> result = await rpc.engine_newPayloadV6(
            emptyPayload,
            blobVersionedHashes: [],
            parentBeaconBlockRoot: Keccak.Zero,
            executionRequests: baselinePayload.Data!.ExecutionRequests,
            inclusionListTransactions: inclusionList);

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success), result.Result.Error);
        Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.InclusionListUnsatisfied));
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

        ResultWrapper<PayloadStatusV1> first = await rpc.engine_newPayloadV6(
            emptyPayload,
            blobVersionedHashes: [],
            parentBeaconBlockRoot: Keccak.Zero,
            executionRequests: payloadResult.Data!.ExecutionRequests,
            inclusionListTransactions: []);
        Assert.That(first.Data.Status, Is.EqualTo(PayloadStatus.Valid));

        // Same block hash, different IL: the cached VALID must not short-circuit the IL check.
        Transaction censoredTx = Build.A.Transaction
            .WithNonce(0)
            .WithMaxFeePerGas(10.GWei)
            .WithMaxPriorityFeePerGas(2.GWei)
            .WithGasLimit(100_000)
            .WithTo(TestItem.AddressA)
            .SignedAndResolved(TestItem.PrivateKeyB)
            .TestObject;
        ResultWrapper<PayloadStatusV1> second = await rpc.engine_newPayloadV6(
            emptyPayload,
            blobVersionedHashes: [],
            parentBeaconBlockRoot: Keccak.Zero,
            executionRequests: payloadResult.Data!.ExecutionRequests,
            inclusionListTransactions: [Rlp.Encode(censoredTx).Bytes]);
        Assert.That(second.Data.Status, Is.EqualTo(PayloadStatus.InclusionListUnsatisfied));
    }

    [Test]
    public async Task NewPayloadV6_should_reject_oversized_inclusion_list()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;

        ResultWrapper<ForkchoiceUpdatedV1Result> fcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
            BuildBogotaPayloadAttributes(inclusionList: []));
        ResultWrapper<GetPayloadV6Result?> payloadResult = await rpc.engine_getPayloadV6(Bytes.FromHexString(fcu.Data.PayloadId!));

        ResultWrapper<PayloadStatusV1> result = await rpc.engine_newPayloadV6(
            payloadResult.Data!.ExecutionPayload,
            blobVersionedHashes: [],
            parentBeaconBlockRoot: Keccak.Zero,
            executionRequests: payloadResult.Data!.ExecutionRequests,
            inclusionListTransactions: [new byte[Eip7805Constants.MaxBytesPerInclusionList + 1]]);

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
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
        ResultWrapper<PayloadStatusV1> verify = await rpc.engine_newPayloadV6(
            payload,
            blobVersionedHashes: [],
            parentBeaconBlockRoot: Keccak.Zero,
            executionRequests: [],
            inclusionListTransactions: [txBytes]);
        Assert.That(verify.Result.ResultType, Is.EqualTo(ResultType.Success), verify.Result.Error);
        Assert.That(verify.Data.Status, Is.EqualTo(PayloadStatus.Valid));
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

        using InclusionListBytes inclusionList = (await rpc.engine_getInclusionListV1(chain.BlockTree.HeadHash)).Data!;

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

    private PayloadAttributes BuildBogotaPayloadAttributes(byte[][] inclusionList) => new()
    {
        Timestamp = Timestamper.UnixTime.Seconds,
        PrevRandao = Keccak.Zero,
        SuggestedFeeRecipient = TestItem.AddressC,
        Withdrawals = [],
        ParentBeaconBlockRoot = Keccak.Zero,
        SlotNumber = 1,
        InclusionListTransactions = inclusionList,
    };
}
