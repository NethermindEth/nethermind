// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using FluentAssertions;
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
    /// EIP-7805 (FOCIL) end-to-end round trip with an empty inclusion list: FCU V5 (which
    /// carries the IL via PayloadAttributesV5) → getPayload V6 → newPayload V6 → FCU V5 to
    /// promote. Empty ILs are trivially satisfied so we expect every step to return
    /// <see cref="PayloadStatus.Valid"/>.
    /// </summary>
    /// <remarks>
    /// Block hashes vary with the state root (which itself depends on the genesis built by
    /// <see cref="MergeTestBlockchain"/>), so we let the engine compute them and chain
    /// responses by hash instead of hard-coding values that would drift the moment Amsterdam
    /// (which Bogota descends from) gains another system contract.
    /// </remarks>
    [Test]
    public async Task Should_process_block_as_expected_Bogota()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;

        PayloadAttributes payloadAttrs = BuildBogotaPayloadAttributes(inclusionList: []);
        // ForkchoiceStateV1 ctor is (head, finalized, safe). Genesis sits in for safe/finalized
        // because there's nothing canonicalised yet.
        ForkchoiceStateV1 fcuState = new(startingHead, Keccak.Zero, startingHead);

        ResultWrapper<ForkchoiceUpdatedV1Result> fcuResult = await rpc.engine_forkchoiceUpdatedV5(fcuState, payloadAttrs);
        fcuResult.Result.ResultType.Should().Be(ResultType.Success, fcuResult.Result.Error);
        fcuResult.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);
        fcuResult.Data.PayloadId.Should().NotBeNull();

        ResultWrapper<GetPayloadV6Result?> payloadResult = await rpc.engine_getPayloadV6(Bytes.FromHexString(fcuResult.Data.PayloadId!));
        payloadResult.Data.Should().NotBeNull();
        ExecutionPayloadV4 executionPayload = payloadResult.Data!.ExecutionPayload;
        executionPayload.Transactions.Should().BeEmpty();

        ResultWrapper<PayloadStatusV1> newPayload = await rpc.engine_newPayloadV6(
            executionPayload,
            blobVersionedHashes: [],
            parentBeaconBlockRoot: Keccak.Zero,
            executionRequests: payloadResult.Data!.ExecutionRequests,
            inclusionListTransactions: []);
        newPayload.Result.ResultType.Should().Be(ResultType.Success, newPayload.Result.Error);
        newPayload.Data.Status.Should().Be(PayloadStatus.Valid);
        newPayload.Data.LatestValidHash.Should().Be(executionPayload.BlockHash);

        // Promote the new block to head, finalized, and safe.
        ResultWrapper<ForkchoiceUpdatedV1Result> finalFcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(executionPayload.BlockHash, executionPayload.BlockHash, executionPayload.BlockHash),
            payloadAttributes: null);
        finalFcu.Result.ResultType.Should().Be(ResultType.Success, finalFcu.Result.Error);
        finalFcu.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);
        finalFcu.Data.PayloadStatus.LatestValidHash.Should().Be(executionPayload.BlockHash);
        finalFcu.Data.PayloadId.Should().BeNull();
    }

    /// <summary>
    /// EIP-7805 (FOCIL): when the CL passes an IL containing a transaction that is valid
    /// against the post-execution state yet absent from the payload, newPayloadV6 must
    /// return <see cref="PayloadStatus.InvalidInclusionList"/> (added by execution-apis#609).
    /// </summary>
    /// <remarks>
    /// We build a baseline empty payload through the engine, then call newPayloadV6 against
    /// that same payload with a one-tx IL whose sender has the funds + nonce to be included.
    /// The validator should reject because the payload trivially had room for the tx.
    /// </remarks>
    [Test]
    public async Task NewPayloadV6_should_return_invalid_for_unsatisfied_inclusion_list_Bogota()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;

        // Build a baseline empty payload — the engine handles BAL/state root computation,
        // so we don't have to recompute hashes when other Amsterdam features change.
        // FCU ctor is (head, finalized, safe).
        ResultWrapper<ForkchoiceUpdatedV1Result> baselineFcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
            BuildBogotaPayloadAttributes(inclusionList: []));
        ResultWrapper<GetPayloadV6Result?> baselinePayload = await rpc.engine_getPayloadV6(Bytes.FromHexString(baselineFcu.Data.PayloadId!));
        ExecutionPayloadV4 emptyPayload = baselinePayload.Data!.ExecutionPayload;

        // Construct a censored tx — a normal Alice→Bob transfer from a funded test account.
        // It would fit comfortably in the empty payload, so the IL constraint is not
        // satisfied and the EL must surface InvalidInclusionList.
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

        result.Result.ResultType.Should().Be(ResultType.Success, result.Result.Error);
        result.Data.Status.Should().Be(PayloadStatus.InvalidInclusionList);
        result.Data.LatestValidHash.Should().Be(emptyPayload.BlockHash);
    }

    /// <summary>
    /// EIP-7805 (FOCIL): supplying inclusion-list transactions through
    /// PayloadAttributesV5 must cause the producer to include them in the next built
    /// payload. <see cref="InclusionListBlockProducerTxSourceFactory"/> prepends the IL
    /// source ahead of the mempool, so an IL tx wins the slot even when the pool is empty.
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

        fcu.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);
        fcu.Data.PayloadId.Should().NotBeNull();

        // With FOCIL, PayloadPreparationService.ProduceEmptyBlock drops the EmptyBlock fast
        // path whenever the CL supplies a non-empty IL, so the very first build already
        // contains the IL transactions. Fetch once via engine_getPayloadV6 — no polling /
        // sleeping needed.
        ResultWrapper<GetPayloadV6Result?> payloadResult = await rpc.engine_getPayloadV6(Bytes.FromHexString(fcu.Data.PayloadId!));
        payloadResult.Data.Should().NotBeNull();
        ExecutionPayloadV4 payload = payloadResult.Data!.ExecutionPayload;
        payload.Transactions.Should().ContainSingle().Which.Should().BeEquivalentTo(txBytes);

        // The block-as-built must round-trip through newPayloadV6 with the same IL.
        ResultWrapper<PayloadStatusV1> verify = await rpc.engine_newPayloadV6(
            payload,
            blobVersionedHashes: [],
            parentBeaconBlockRoot: Keccak.Zero,
            executionRequests: [],
            inclusionListTransactions: [txBytes]);
        verify.Result.ResultType.Should().Be(ResultType.Success, verify.Result.Error);
        verify.Data.Status.Should().Be(PayloadStatus.Valid);
    }

    /// <summary>
    /// EIP-7805 (FOCIL): the IL builder drains the local txpool and returns the encoded
    /// transaction bytes, bounded by <c>MAX_BYTES_PER_INCLUSION_LIST</c> and
    /// <c>MAX_TRANSACTIONS_PER_INCLUSION_LIST</c>. The <c>blockHash</c> parameter is required
    /// by the spec (execution-apis#609) but not consulted by the current selection strategy.
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

    /// <summary>
    /// Shared payload-attributes template for the Bogota tests above. PayloadAttributesV5
    /// (the wire shape behind <c>engine_forkchoiceUpdatedV5</c>) requires <c>slotNumber</c>
    /// and <c>inclusionListTransactions</c>; the rest mirrors Amsterdam.
    /// </summary>
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
