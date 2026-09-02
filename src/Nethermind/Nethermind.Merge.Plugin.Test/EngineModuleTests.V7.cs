// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test;
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

        ResultWrapper<ForkchoiceUpdatedV2Result> fcuResult = await rpc.engine_forkchoiceUpdatedV5(fcuState, payloadAttrs);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fcuResult.Result.ResultType, Is.EqualTo(ResultType.Success), fcuResult.Result.Error);
            Assert.That(fcuResult.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(fcuResult.Data.PayloadId, Is.Not.Null);
        }

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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(newPayload.Result.ResultType, Is.EqualTo(ResultType.Success), newPayload.Result.Error);
            Assert.That(newPayload.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(newPayload.Data.InclusionListSatisfied, Is.True);
            Assert.That(newPayload.Data.LatestValidHash, Is.EqualTo(executionPayload.BlockHash));
        }

        ResultWrapper<ForkchoiceUpdatedV2Result> finalFcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(executionPayload.BlockHash, executionPayload.BlockHash, executionPayload.BlockHash),
            payloadAttributes: null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(finalFcu.Result.ResultType, Is.EqualTo(ResultType.Success), finalFcu.Result.Error);
            Assert.That(finalFcu.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(finalFcu.Data.PayloadStatus.LatestValidHash, Is.EqualTo(executionPayload.BlockHash));
            Assert.That(finalFcu.Data.PayloadId, Is.Null);
            // execution-apis#609: FCU V5 reports the head's inclusion-list compliance retained from newPayloadV6.
            Assert.That(finalFcu.Data.PayloadStatus.InclusionListSatisfied, Is.True);
        }
    }

    [Test]
    public async Task ForkchoiceUpdatedV5_reports_head_inclusion_list_unsatisfied()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;

        ResultWrapper<ForkchoiceUpdatedV2Result> build = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
            BuildBogotaPayloadAttributes(inclusionList: []));
        ResultWrapper<GetPayloadV6Result?> payloadResult = await rpc.engine_getPayloadV6(Bytes.FromHexString(build.Data.PayloadId!));
        ExecutionPayloadV4 emptyPayload = payloadResult.Data!.ExecutionPayload;

        // Deliver the empty block with a censoring IL → newPayloadV6 retains inclusionListSatisfied=false.
        Transaction censoredTx = Build.A.Transaction
            .WithNonce(0).WithMaxFeePerGas(10.GWei).WithMaxPriorityFeePerGas(2.GWei).WithGasLimit(100_000)
            .WithTo(TestItem.AddressA).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        ResultWrapper<PayloadStatusV2> np = await rpc.engine_newPayloadV6(
            emptyPayload, [], Keccak.Zero, payloadResult.Data!.ExecutionRequests, [Rlp.Encode(censoredTx).Bytes]);
        Assert.That(np.Data.InclusionListSatisfied, Is.False);

        // FCU V5 to that VALID head reports the retained compliance.
        ResultWrapper<ForkchoiceUpdatedV2Result> fcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(emptyPayload.BlockHash, startingHead, startingHead),
            payloadAttributes: null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fcu.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(fcu.Data.PayloadStatus.InclusionListSatisfied, Is.False);
        }
    }

    [Test]
    public async Task NewPayloadV6_should_report_unsatisfied_inclusion_list()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;

        // Let the engine compute the hashes so the test stays stable across unrelated changes.
        ResultWrapper<ForkchoiceUpdatedV2Result> baselineFcu = await rpc.engine_forkchoiceUpdatedV5(
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success), result.Result.Error);
            Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(result.Data.InclusionListSatisfied, Is.False);
            Assert.That(result.Data.LatestValidHash, Is.EqualTo(emptyPayload.BlockHash));
        }
    }

    [Test]
    public async Task NewPayloadV6_should_revalidate_same_block_against_new_inclusion_list()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;

        ResultWrapper<ForkchoiceUpdatedV2Result> fcu = await rpc.engine_forkchoiceUpdatedV5(
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(first.Data.InclusionListSatisfied, Is.True);
        }

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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(second.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(second.Data.InclusionListSatisfied, Is.False);
        }
    }

    [Test]
    public async Task NewPayloadV6_accepts_aggregate_inclusion_list_exceeding_single_member_cap()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;

        ResultWrapper<ForkchoiceUpdatedV2Result> fcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
            BuildBogotaPayloadAttributes(inclusionList: []));
        ResultWrapper<GetPayloadV6Result?> payloadResult = await rpc.engine_getPayloadV6(Bytes.FromHexString(fcu.Data.PayloadId!));

        // The flattened aggregate can exceed the per-member cap; newPayloadV6 must not reject it.
        byte[] member = new byte[Eip7805Constants.MaxBytesPerInclusionList * 3 / 4];
        ResultWrapper<PayloadStatusV2> result = await rpc.engine_newPayloadV6(
            payloadResult.Data!.ExecutionPayload,
            blobVersionedHashes: [],
            parentBeaconBlockRoot: Keccak.Zero,
            executionRequests: payloadResult.Data!.ExecutionRequests,
            inclusionListTransactions: [member, member]);

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
    }

    // Consensus gossip caps each member's list by bytes, and an empty entry costs only its SSZ offset, so
    // a conforming member can carry ~2,048 entries. An aggregate of such members must not be rejected.
    [Test]
    public async Task NewPayloadV6_accepts_an_aggregate_of_entry_dense_conforming_members()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;

        ResultWrapper<ForkchoiceUpdatedV2Result> fcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
            BuildBogotaPayloadAttributes(inclusionList: []));
        ResultWrapper<GetPayloadV6Result?> payloadResult = await rpc.engine_getPayloadV6(Bytes.FromHexString(fcu.Data.PayloadId!));

        // Three members of 1,400 empty entries each: 5,600 SSZ bytes apiece, well inside the per-member cap.
        byte[][] aggregate = new byte[3 * 1400][];
        for (int i = 0; i < aggregate.Length; i++) aggregate[i] = [];

        ResultWrapper<PayloadStatusV2> result = await rpc.engine_newPayloadV6(
            payloadResult.Data!.ExecutionPayload, [], Keccak.Zero, payloadResult.Data!.ExecutionRequests, aggregate);

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success), result.Result.Error);
    }

    [Test]
    public async Task NewPayloadV6_bounds_aggregate_inclusion_list_bytes()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;

        ResultWrapper<ForkchoiceUpdatedV2Result> fcu = await rpc.engine_forkchoiceUpdatedV5(
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(overLimit.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(overLimit.Result.Error, Does.Contain("exceeds the maximum aggregate size"));
        }

        // Entry count is bounded independently of bytes — empty entries cost no bytes but still allocate.
        byte[][] tooManyEmpty = new byte[Eip7805Constants.MaxAggregateInclusionListTransactions + 1][];
        for (int i = 0; i < tooManyEmpty.Length; i++) tooManyEmpty[i] = [];
        ResultWrapper<PayloadStatusV2> tooManyEntries = await rpc.engine_newPayloadV6(
            emptyPayload, [], Keccak.Zero, payloadResult.Data!.ExecutionRequests, tooManyEmpty);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tooManyEntries.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(tooManyEntries.Result.Error, Does.Contain("maximum number of transactions"));
        }
    }

    [Test]
    public async Task NewPayloadV5_is_unsupported_at_Bogota()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;

        ResultWrapper<ForkchoiceUpdatedV2Result> fcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
            BuildBogotaPayloadAttributes(inclusionList: []));
        ResultWrapper<GetPayloadV6Result?> payloadResult = await rpc.engine_getPayloadV6(Bytes.FromHexString(fcu.Data.PayloadId!));

        // execution-apis#609: at/after Bogota, engine_newPayloadV5 must be rejected with -38005.
        ResultWrapper<PayloadStatusV1> result = await rpc.engine_newPayloadV5(
            payloadResult.Data!.ExecutionPayload, [], Keccak.Zero, payloadResult.Data!.ExecutionRequests);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(result.ErrorCode, Is.EqualTo(MergeErrorCodes.UnsupportedFork));
        }
    }

    // The witness wrapper delegates to a newPayload version, so rejecting V5 would leave it without an
    // entry point unless it gains its own V6.
    [Test]
    public async Task NewPayloadWithWitnessV6_supersedes_V5_at_Bogota()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;

        ResultWrapper<ForkchoiceUpdatedV2Result> fcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
            BuildBogotaPayloadAttributes(inclusionList: []));
        ResultWrapper<GetPayloadV6Result?> payloadResult = await rpc.engine_getPayloadV6(Bytes.FromHexString(fcu.Data.PayloadId!));
        ExecutionPayloadV4 executionPayload = payloadResult.Data!.ExecutionPayload;
        byte[][]? executionRequests = payloadResult.Data!.ExecutionRequests;

        using ResultWrapper<NewPayloadWithWitnessV1Result> v5 = await rpc.engine_newPayloadWithWitnessV5(
            executionPayload, [], Keccak.Zero, executionRequests);
        using ResultWrapper<NewPayloadWithWitnessV1Result> v6 = await rpc.engine_newPayloadWithWitnessV6(
            executionPayload, [], Keccak.Zero, executionRequests, []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(v5.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(v5.ErrorCode, Is.EqualTo(MergeErrorCodes.UnsupportedFork));
            Assert.That(v6.Result.ResultType, Is.EqualTo(ResultType.Success), v6.Result.Error);
            Assert.That(v6.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(v6.Data.LatestValidHash, Is.EqualTo(executionPayload.BlockHash));
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(result.ErrorCode, Is.EqualTo(MergeErrorCodes.UnsupportedFork));
        }
    }

    // bogota.md: PayloadAttributesV5 appends inclusionListTransactions unconditionally, so an empty list is
    // how a proposer says it has none — omitting the field leaves the attributes V4-shaped.
    [Test]
    public async Task ForkchoiceUpdatedV5_rejects_attributes_without_an_inclusion_list()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;

        ResultWrapper<ForkchoiceUpdatedV2Result> missing = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
            BuildBogotaPayloadAttributes(inclusionList: null!));
        ResultWrapper<ForkchoiceUpdatedV2Result> empty = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
            BuildBogotaPayloadAttributes(inclusionList: []));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(missing.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(missing.ErrorCode, Is.EqualTo(MergeErrorCodes.InvalidPayloadAttributes));
            Assert.That(empty.Result.ResultType, Is.EqualTo(ResultType.Success), empty.Result.Error);
            Assert.That(empty.Data.PayloadId, Is.Not.Null);
        }
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

        ResultWrapper<ForkchoiceUpdatedV2Result> fcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
            BuildBogotaPayloadAttributes(inclusionList: [txBytes]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fcu.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(fcu.Data.PayloadId, Is.Not.Null);
        }

        // Even the producer's EmptyBlock fast path carries the IL, so the first getPayload is populated.
        ResultWrapper<GetPayloadV6Result?> payloadResult = await rpc.engine_getPayloadV6(Bytes.FromHexString(fcu.Data.PayloadId!));
        Assert.That(payloadResult.Data, Is.Not.Null);
        ExecutionPayloadV4 payload = payloadResult.Data!.ExecutionPayload;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(payload.Transactions, Has.Length.EqualTo(1));
            Assert.That(payload.Transactions[0], Is.EqualTo(txBytes));
        }

        // The block-as-built must round-trip through newPayloadV6 with the same IL.
        ResultWrapper<PayloadStatusV2> verify = await rpc.engine_newPayloadV6(
            payload,
            blobVersionedHashes: [],
            parentBeaconBlockRoot: Keccak.Zero,
            executionRequests: [],
            inclusionListTransactions: [txBytes]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(verify.Result.ResultType, Is.EqualTo(ResultType.Success), verify.Result.Error);
            Assert.That(verify.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(verify.Data.InclusionListSatisfied, Is.True);
        }
    }

    [Test]
    public async Task NewPayloadV6_resubmitting_canonical_block_with_same_inclusion_list_stays_valid()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;

        Transaction tx = Build.A.Transaction
            .WithNonce(0).WithMaxFeePerGas(10.GWei).WithMaxPriorityFeePerGas(2.GWei)
            .WithTo(TestItem.AddressA).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        byte[][] inclusionList = [Rlp.Encode(tx).Bytes];

        ResultWrapper<ForkchoiceUpdatedV2Result> fcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
            BuildBogotaPayloadAttributes(inclusionList: inclusionList));
        ResultWrapper<GetPayloadV6Result?> payloadResult = await rpc.engine_getPayloadV6(Bytes.FromHexString(fcu.Data.PayloadId!));
        ExecutionPayloadV4 payload = payloadResult.Data!.ExecutionPayload;

        ResultWrapper<PayloadStatusV2> first = await rpc.engine_newPayloadV6(
            payload, [], Keccak.Zero, payloadResult.Data!.ExecutionRequests, inclusionList);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(first.Data.InclusionListSatisfied, Is.True);
        }

        // Promote to canonical head, then re-submit the same (block, IL).
        await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(payload.BlockHash, payload.BlockHash, payload.BlockHash), payloadAttributes: null);

        // The re-submission must reuse the cached result (VALID + satisfied), not regress to SYNCING or re-execute.
        ResultWrapper<PayloadStatusV2> resend = await rpc.engine_newPayloadV6(
            payload, [], Keccak.Zero, payloadResult.Data!.ExecutionRequests, inclusionList);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resend.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(resend.Data.InclusionListSatisfied, Is.True);
        }
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

        ResultWrapper<ForkchoiceUpdatedV2Result> fcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
            BuildBogotaPayloadAttributes(inclusionList: inclusionList));
        ResultWrapper<GetPayloadV6Result?> payloadResult = await rpc.engine_getPayloadV6(Bytes.FromHexString(fcu.Data.PayloadId!));
        ExecutionPayloadV4 payload = payloadResult.Data!.ExecutionPayload;

        // Both IL txs must be produced, in ascending-nonce order.
        Assert.That(payload.Transactions, Has.Length.EqualTo(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(payload.Transactions[0], Is.EqualTo(Rlp.Encode(tx0).Bytes));
            Assert.That(payload.Transactions[1], Is.EqualTo(Rlp.Encode(tx1).Bytes));
        }
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

    // The consensus layer names the block the list is requested for, so the parameter has to reach the
    // handler: dispatched as a no-argument method the call answers -32602 and no list is ever built.
    [TestCase(false, TestName = "GetInclusionListV1_without_a_parent_block_hash_builds_on_the_head")]
    [TestCase(true, TestName = "GetInclusionListV1_accepts_the_parent_block_hash")]
    public async Task GetInclusionListV1_serves_both_request_shapes(bool withParentBlockHash)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance);
        object?[] parameters = withParentBlockHash ? [chain.BlockTree.HeadHash] : [];

        string response = await RpcTest.TestSerializedRequest(chain.EngineRpcModule,
            nameof(IEngineRpcModule.engine_getInclusionListV1), parameters);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response, Does.Contain("\"result\""));
            Assert.That(response, Does.Not.Contain("\"error\""));
        }
    }

    // A list built on a block this node does not have could not be appended to it.
    [Test]
    public async Task GetInclusionListV1_rejects_an_unknown_parent_block_hash()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance);

        ResultWrapper<InclusionListBytes> result =
            await chain.EngineRpcModule.engine_getInclusionListV1(TestItem.KeccakA);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
        }
    }

    [Test]
    public async Task GetInclusionListV1_before_Bogota_is_unsupported_fork()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;

        ResultWrapper<InclusionListBytes> result = await rpc.engine_getInclusionListV1();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(result.ErrorCode, Is.EqualTo(MergeErrorCodes.UnsupportedFork));
        }
    }

    // The fallback payload has to satisfy the inclusion list, but must not pay for a mempool selection:
    // it is produced synchronously on the engine thread while every other engine call waits.
    [Test]
    public async Task Empty_block_fallback_carries_the_inclusion_list_without_the_mempool()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;

        Transaction inclusionListTx = Build.A.Transaction
            .WithNonce(0).WithMaxFeePerGas(10.GWei).WithMaxPriorityFeePerGas(2.GWei)
            .WithTo(TestItem.AddressA).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Transaction mempoolTx = Build.A.Transaction
            .WithNonce(0).WithMaxFeePerGas(10.GWei).WithMaxPriorityFeePerGas(2.GWei)
            .WithTo(TestItem.AddressA).SignedAndResolved(TestItem.PrivateKeyC).TestObject;
        Assert.That(chain.TxPool.SubmitTx(mempoolTx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

        PayloadAttributes payloadAttributes = BuildBogotaPayloadAttributes(inclusionList: [Rlp.Encode(inclusionListTx).Bytes]);
        await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead), payloadAttributes);

        Block? emptyBlock = await chain.BlockProducer!.BuildBlock(
            chain.BlockTree.Head!.Header, payloadAttributes: payloadAttributes, flags: IBlockProducer.Flags.PrepareEmptyBlock);

        Assert.That(emptyBlock!.Transactions.Select(t => t.Hash), Is.EqualTo(new[] { inclusionListTx.Hash }));
    }

    // bogota.md newPayloadV6 (2.1): a VALID response must carry a compliance answer. Appendability is
    // judged against the state the block committed, so a canonical block is answerable without the
    // re-execution that would replay the whole pruning window on every resend.
    [Test]
    public async Task NewPayloadV6_answers_an_inclusion_list_behind_head_without_re_executing()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;

        ExecutionPayloadV4 first = await BuildAndInsertEmptyBlock(rpc, chain.BlockTree.HeadHash, slot: 2);
        await BuildAndInsertEmptyBlock(rpc, first.BlockHash, slot: 3);

        // A different IL misses the (block, IL) cache, and the first block is now behind head.
        Transaction censoredTx = Build.A.Transaction
            .WithNonce(0).WithMaxFeePerGas(10.GWei).WithMaxPriorityFeePerGas(2.GWei)
            .WithTo(TestItem.AddressA).SignedAndResolved(TestItem.PrivateKeyB).TestObject;

        int processed = 0;
        chain.BranchProcessor.BlockProcessing += (_, _) => Interlocked.Increment(ref processed);
        ResultWrapper<PayloadStatusV2> resend = await rpc.engine_newPayloadV6(
            first, [], Keccak.Zero, [], [Rlp.Encode(censoredTx).Bytes]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resend.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(resend.Data.InclusionListSatisfied, Is.False);
            Assert.That(processed, Is.Zero, "the block must not be re-executed");
        }
    }

    private async Task<ExecutionPayloadV4> BuildAndInsertEmptyBlock(IEngineRpcModule rpc, Hash256 parent, ulong slot)
    {
        ResultWrapper<ForkchoiceUpdatedV2Result> fcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(parent, Keccak.Zero, parent),
            BuildBogotaPayloadAttributes(inclusionList: [], timestamp: Timestamper.UnixTime.Seconds + slot, slotNumber: slot));
        ResultWrapper<GetPayloadV6Result?> payloadResult = await rpc.engine_getPayloadV6(Bytes.FromHexString(fcu.Data.PayloadId!));
        ExecutionPayloadV4 payload = payloadResult.Data!.ExecutionPayload;

        await rpc.engine_newPayloadV6(payload, [], Keccak.Zero, payloadResult.Data!.ExecutionRequests, []);
        await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(payload.BlockHash, payload.BlockHash, payload.BlockHash), payloadAttributes: null);
        return payload;
    }

    private PayloadAttributes BuildBogotaPayloadAttributes(byte[][] inclusionList, ulong targetGasLimit = 30_000_000UL, ulong? timestamp = null, ulong slotNumber = 1) => new()
    {
        Timestamp = timestamp ?? Timestamper.UnixTime.Seconds,
        PrevRandao = Keccak.Zero,
        SuggestedFeeRecipient = TestItem.AddressC,
        Withdrawals = [],
        ParentBeaconBlockRoot = Keccak.Zero,
        SlotNumber = slotNumber,
        // V4 attributes require TargetGasLimit.
        TargetGasLimit = targetGasLimit,
        InclusionListTransactions = inclusionList,
    };
}
