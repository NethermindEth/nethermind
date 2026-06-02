// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    /// <summary>
    /// Bogota end-to-end with an empty IL: FCUv5 → getPayloadV6 → newPayloadV6 → promote FCU.
    /// Empty ILs are trivially satisfied so every step must return <see cref="PayloadStatus.Valid"/>.
    /// </summary>
    [Test]
    public async Task Should_process_block_as_expected_Bogota()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;

        PayloadAttributes payloadAttrs = BuildBogotaPayloadAttributes(inclusionList: []);
        // ForkchoiceStateV1 ctor is (head, finalized, safe); genesis stands in for both here.
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

    /// <summary>
    /// FOCIL: an IL tx that's appendable against parent state but missing from the payload
    /// must produce <see cref="PayloadStatus.InvalidInclusionList"/>.
    /// </summary>
    [Test]
    public async Task NewPayloadV6_should_return_invalid_for_unsatisfied_inclusion_list_Bogota()
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
        Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.InvalidInclusionList));
        Assert.That(result.Data.LatestValidHash, Is.EqualTo(emptyPayload.BlockHash));
    }

    /// <summary>
    /// FOCIL: IL txs supplied via PayloadAttributesV5 must end up in the next built payload —
    /// the IL source is prepended ahead of the mempool by the producer-tx-source factory.
    /// </summary>
    [Test]
    public async Task Should_build_block_with_inclusion_list_transactions_Bogota()
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

    /// <summary>
    /// engine_getInclusionListV1 returns encoded mempool txs, bounded by the spec's MAX_BYTES
    /// and MAX_TXS constants. blockHash is spec-required but not consulted today.
    /// </summary>
    [Test]
    public async Task Can_get_inclusion_list_Bogota()
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

        using ArrayPoolList<byte[]>? inclusionList = (await rpc.engine_getInclusionListV1(chain.BlockTree.HeadHash)).Data;

        byte[] tx1Bytes = Rlp.Encode(tx1).Bytes;
        byte[] tx2Bytes = Rlp.Encode(tx2).Bytes;

        Assert.Multiple(() =>
        {
            Assert.That(inclusionList, Is.Not.Null);
            Assert.That(inclusionList!.Count, Is.EqualTo(2));
            Assert.That(inclusionList, Does.Contain(tx1Bytes));
            Assert.That(inclusionList, Does.Contain(tx2Bytes));
        });
    }

    /// <summary>Shared PayloadAttributesV5 template — slotNumber + IL required, rest mirrors Amsterdam.</summary>
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
