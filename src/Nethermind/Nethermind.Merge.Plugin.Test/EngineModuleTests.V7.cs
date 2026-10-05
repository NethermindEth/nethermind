// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using Nethermind.Trie;
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
    public async Task NewPayloadV6_accepts_empty_hex_logs_bloom_of_log_less_block([Values] bool eip7668)
    {
        IReleaseSpec spec = eip7668 ? new OverridableReleaseSpec(Bogota.Instance) { IsEip7668Enabled = true } : Bogota.Instance;
        using MergeTestBlockchain chain = await CreateBlockchain(spec, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;
        ResultWrapper<ForkchoiceUpdatedV2Result> fcuResult = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead), BuildBogotaPayloadAttributes(inclusionList: []));
        ResultWrapper<GetPayloadV6Result?> payloadResult = await rpc.engine_getPayloadV6(Bytes.FromHexString(fcuResult.Data.PayloadId!));

        // Before EIP-7668 "0x" reads as the zero bloom, so the hash computed over 256 zero bytes still matches.
        JsonNode payloadJson = JsonNode.Parse(chain.JsonSerializer.Serialize(payloadResult.Data!.ExecutionPayload))!;
        payloadJson["logsBloom"] = "0x";
        ExecutionPayloadV4 executionPayload = chain.JsonSerializer.Deserialize<ExecutionPayloadV4>(payloadJson.ToJsonString())!;

        ResultWrapper<PayloadStatusV2> newPayload = await rpc.engine_newPayloadV6(
            executionPayload, [], Keccak.Zero, payloadResult.Data!.ExecutionRequests, []);

        Assert.That(newPayload.Data.Status, Is.EqualTo(PayloadStatus.Valid), newPayload.Data.ValidationError);
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

    // Also pins that forkchoiceUpdatedV5 reports the answer for the latest list, whichever order they arrive in.
    [Test]
    public async Task NewPayloadV6_should_revalidate_same_block_against_new_inclusion_list([Values] bool censoringListLast)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;

        ResultWrapper<ForkchoiceUpdatedV2Result> fcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
            BuildBogotaPayloadAttributes(inclusionList: []));
        ResultWrapper<GetPayloadV6Result?> payloadResult = await rpc.engine_getPayloadV6(Bytes.FromHexString(fcu.Data.PayloadId!));
        ExecutionPayloadV4 emptyPayload = payloadResult.Data!.ExecutionPayload;
        byte[][] censoringList = [Rlp.Encode(BuildInclusionListTransfer()).Bytes];

        ResultWrapper<PayloadStatusV2> first = await rpc.engine_newPayloadV6(
            emptyPayload,
            blobVersionedHashes: [],
            parentBeaconBlockRoot: Keccak.Zero,
            executionRequests: payloadResult.Data!.ExecutionRequests,
            inclusionListTransactions: censoringListLast ? [] : censoringList);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(first.Data.InclusionListSatisfied, Is.EqualTo(censoringListLast));
        }

        // Same block hash, different IL: the cached VALID must not short-circuit the IL check.
        ResultWrapper<PayloadStatusV2> second = await rpc.engine_newPayloadV6(
            emptyPayload,
            blobVersionedHashes: [],
            parentBeaconBlockRoot: Keccak.Zero,
            executionRequests: payloadResult.Data!.ExecutionRequests,
            inclusionListTransactions: censoringListLast ? censoringList : []);
        ResultWrapper<ForkchoiceUpdatedV2Result> toBlock = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(emptyPayload.BlockHash, startingHead, startingHead), payloadAttributes: null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(second.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(second.Data.InclusionListSatisfied, Is.EqualTo(!censoringListLast));
            Assert.That(toBlock.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(toBlock.Data.PayloadStatus.InclusionListSatisfied, Is.EqualTo(!censoringListLast));
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

        PayloadAttributes attrs = CreateAmsterdamPayloadAttributes(genesis.Header);
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
    public async Task NewPayloadV6_resubmitting_canonical_block_with_same_inclusion_list_requires_state([Values] bool statePruned)
    {
        ConcurrentDictionary<Hash256, byte> pruned = new();
        using MergeTestBlockchain chain = await CreateBlockchainWithPrunableState(pruned, releaseSpec: Bogota.Instance);
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
        if (statePruned) pruned[payload.BlockHash] = 0;

        // A cached result can only be reused while the state that established compliance is available.
        ResultWrapper<PayloadStatusV2> resend = await rpc.engine_newPayloadV6(
            payload, [], Keccak.Zero, payloadResult.Data!.ExecutionRequests, inclusionList);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resend.Data.Status, Is.EqualTo(statePruned ? PayloadStatus.Syncing : PayloadStatus.Valid));
            if (!statePruned) Assert.That(resend.Data.InclusionListSatisfied, Is.True);
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

    // The zero hash names no block, so it must fall back to the head rather than being looked up.
    [Test]
    public async Task GetInclusionListV1_treats_the_zero_parent_block_hash_as_unspecified()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance);

        ResultWrapper<InclusionListBytes> result =
            await chain.EngineRpcModule.engine_getInclusionListV1(Hash256.Zero);
        result.Data?.Dispose();

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success), result.Result.Error);
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

    // bogota.md newPayloadV6 (2.1): a VALID response must carry a compliance answer. A canonical block this node ran
    // is answered from its own state; otherwise it is answered SYNCING rather than re-executed.
    [TestCase(true, false, PayloadStatus.Valid)]
    [TestCase(true, true, PayloadStatus.Syncing)]
    [TestCase(false, false, PayloadStatus.Syncing)]
    public async Task NewPayloadV6_answers_an_inclusion_list_behind_head_from_its_state_only_when_run_here(
        bool runHere, bool statePruned, string expected)
    {
        ConcurrentDictionary<Hash256, byte> pruned = new();
        using MergeTestBlockchain chain = await CreateBlockchainWithPrunableState(pruned, releaseSpec: Bogota.Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;

        ExecutionPayloadV4 first = await BuildAndInsertEmptyBlock(rpc, chain.BlockTree.HeadHash, slot: 2);
        ExecutionPayloadV4 second = await BuildAndInsertEmptyBlock(rpc, first.BlockHash, slot: 3);
        ulong bestKnown = chain.BlockTree.BestKnownNumber;
        // What the pre-pivot header backfill leaves behind: on the main chain, never run by this node.
        if (!runHere) chain.BlockTree.GetInfo(first.BlockNumber, first.BlockHash).Info!.WasProcessed = false;
        if (statePruned) pruned[first.BlockHash] = 0;

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
            Assert.That(resend.Data.Status, Is.EqualTo(expected),
                runHere && !statePruned ? "a block this node ran is answered from its state" : "a block without its own state is not answered from a state root");
            if (expected == PayloadStatus.Valid) Assert.That(resend.Data.InclusionListSatisfied, Is.False);
            Assert.That(processed, Is.Zero, "a block on the node's own chain is not re-executed");
            Assert.That(chain.BlockTree.BestKnownNumber, Is.EqualTo(bestKnown));
            foreach (ExecutionPayloadV4 block in new[] { first, second })
            {
                Assert.That(chain.BlockTree.FindBlock(block.BlockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded), Is.Not.Null, $"block {block.BlockNumber}");
            }
        }
    }

    /// <summary>
    /// The inclusion-list counterpart of <c>newPayloadV1_answers_valid_for_a_head_resent_while_its_re_execution_commits</c>:
    /// the resent head waits for its commit and has the new list judged against the restored state.
    /// </summary>
    [Test]
    public async Task NewPayloadV6_judges_an_inclusion_list_for_a_head_resent_while_its_re_execution_commits()
    {
        ConcurrentDictionary<Hash256, byte> pruned = new();
        CommitWaitProbe probe = new();
        using MergeTestBlockchain chain = await CreateBlockchainWithPrunableState(pruned,
            builder => builder.AddDecorator<IBlockProcessingQueue>((_, inner) => new CommitWaitObservingQueue(inner, probe)),
            Bogota.Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 genesisHash = chain.BlockTree.HeadHash!;

        // Built and made head without finalizing it, so the head can move back below it.
        ResultWrapper<ForkchoiceUpdatedV2Result> fcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(genesisHash, Keccak.Zero, Keccak.Zero),
            BuildBogotaPayloadAttributes(inclusionList: [], timestamp: Timestamper.UnixTime.Seconds + 2, slotNumber: 2));
        ResultWrapper<GetPayloadV6Result?> built = await rpc.engine_getPayloadV6(Bytes.FromHexString(fcu.Data.PayloadId!));
        ExecutionPayloadV4 block = built.Data!.ExecutionPayload;
        byte[][] requests = built.Data!.ExecutionRequests!;
        Assert.That((await rpc.engine_newPayloadV6(block, [], Keccak.Zero, requests, [])).Data.Status, Is.EqualTo(PayloadStatus.Valid));
        await rpc.engine_forkchoiceUpdatedV5(new ForkchoiceStateV1(block.BlockHash, Keccak.Zero, Keccak.Zero), payloadAttributes: null);

        // The Hive shape: the head moves back, the block's state is pruned, and the block is sent again and re-executed.
        await rpc.engine_forkchoiceUpdatedV5(new ForkchoiceStateV1(genesisHash, Keccak.Zero, Keccak.Zero), payloadAttributes: null);
        pruned[block.BlockHash] = 0;

        // Holds the processing thread between the re-execution's verdict and its commit.
        TaskCompletionSource commit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        chain.Container.Resolve<IBlockProcessingQueue>().BlockExecuted += (_, e) =>
        {
            if (e.BlockHash == block.BlockHash) commit.Task.Wait(TimeSpan.FromSeconds(30));
        };

        Assert.That((await rpc.engine_newPayloadV6(block, [], Keccak.Zero, requests, [])).Data.Status, Is.EqualTo(PayloadStatus.Valid));
        using (Assert.EnterMultipleScope())
        {
            ResultWrapper<ForkchoiceUpdatedV2Result> toBlock = await rpc.engine_forkchoiceUpdatedV5(
                new ForkchoiceStateV1(block.BlockHash, Keccak.Zero, Keccak.Zero), payloadAttributes: null);
            Assert.That(toBlock.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(chain.BlockTree.Head!.Hash, Is.EqualTo(block.BlockHash));
        }

        // A different list misses the (block, IL) cache; release the commit once the re-send waits on it or answers.
        Transaction censoredTx = Build.A.Transaction
            .WithNonce(0).WithMaxFeePerGas(10.GWei).WithMaxPriorityFeePerGas(2.GWei)
            .WithTo(TestItem.AddressA).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        probe.Watch(block.BlockHash);
        Task<ResultWrapper<PayloadStatusV2>> resent = rpc.engine_newPayloadV6(block, [], Keccak.Zero, requests, [Rlp.Encode(censoredTx).Bytes]);
        await Task.WhenAny(probe.WaitEntered.Task, resent, Task.Delay(TimeSpan.FromSeconds(20)));
        bool waitedForCommit = probe.WaitEntered.Task.IsCompleted;
        commit.SetResult();
        ResultWrapper<PayloadStatusV2> result = await resent;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(waitedForCommit, Is.True, "the re-send must wait for the head's committing re-execution");
            Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid),
                "the head's own re-execution is committing, so its list can be judged against the state it restores");
            Assert.That(result.Data.InclusionListSatisfied, Is.False);
        }
    }

    // bogota.md engine_forkchoiceUpdatedV5 (2.2): a VALID head is answered from the list retained when newPayloadV6
    // could not answer. stateReadThrows: a missing trie node while evaluating must not fail the update.
    [TestCase(PayloadStatus.Syncing, true, false, TestName = "ForkchoiceUpdatedV5_answers_a_satisfied_head_left_syncing_by_newPayloadV6")]
    [TestCase(PayloadStatus.Syncing, false, false, TestName = "ForkchoiceUpdatedV5_answers_an_unsatisfied_head_left_syncing_by_newPayloadV6")]
    [TestCase(PayloadStatus.Syncing, false, true, TestName = "ForkchoiceUpdatedV5_reports_null_when_the_retained_list_evaluation_throws")]
    [TestCase(PayloadStatus.Accepted, true, false, TestName = "ForkchoiceUpdatedV5_answers_a_satisfied_head_left_accepted_by_newPayloadV6")]
    [TestCase(PayloadStatus.Accepted, false, false, TestName = "ForkchoiceUpdatedV5_answers_an_unsatisfied_head_left_accepted_by_newPayloadV6")]
    [NonParallelizable]
    public async Task ForkchoiceUpdatedV5_answers_compliance_for_a_head_left_unanswered(
        string newPayloadStatus, bool satisfied, bool stateReadThrows)
    {
        HeadStateInterceptor headState = new();
        StatusOverridingNewPayloadHandler newPayloadHandler = new();
        using MergeTestBlockchain chain = await CreateBlockchainWithHeadState(headState,
            new MergeConfig { TerminalTotalDifficulty = "0", NewPayloadBlockProcessingTimeout = 100 },
            builder => builder.AddDecorator<IAsyncHandler<ExecutionPayload, PayloadStatusV1>>((_, inner) =>
            {
                newPayloadHandler.Inner = inner;
                return newPayloadHandler;
            }));
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Block parent = chain.BlockTree.Head!;
        byte[][] inclusionList = [Rlp.Encode(BuildInclusionListTransfer()).Bytes];

        // Build with the list only in the satisfied case; the other build censors it.
        ResultWrapper<ForkchoiceUpdatedV2Result> build = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(parent.Hash!, Keccak.Zero, parent.Hash!),
            BuildBogotaPayloadAttributes(inclusionList: satisfied ? inclusionList : []));
        ResultWrapper<GetPayloadV6Result?> payloadResult = await rpc.engine_getPayloadV6(Bytes.FromHexString(build.Data.PayloadId!));
        ExecutionPayloadV4 payload = payloadResult.Data!.ExecutionPayload;
        Assert.That(payload.Transactions, Has.Length.EqualTo(satisfied ? 1 : 0));

        // This node never answers ACCEPTED itself, so that status is forced on an otherwise processed payload.
        TaskCompletionSource releaseProcessing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TestBranchProcessorInterceptor branchProcessor = (TestBranchProcessorInterceptor)chain.BranchProcessor;
        try
        {
            if (newPayloadStatus == PayloadStatus.Syncing)
            {
                OccupyBlockProcessor(chain, parent, releaseProcessing.Task);
            }
            else newPayloadHandler.Status = newPayloadStatus;

            ResultWrapper<PayloadStatusV2> newPayload = await rpc.engine_newPayloadV6(
                payload, [], Keccak.Zero, payloadResult.Data!.ExecutionRequests, inclusionList);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(newPayload.Data.Status, Is.EqualTo(newPayloadStatus));
                Assert.That(newPayload.Data.InclusionListSatisfied, Is.Null);
            }
        }
        finally
        {
            releaseProcessing.TrySetResult();
            branchProcessor.ProcessingRelease = null;
            newPayloadHandler.Status = null;
        }
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(20));
        while (!chain.BlockTree.WasProcessed(payload.BlockNumber, payload.BlockHash))
        {
            await Task.Delay(20, cts.Token);
        }

        if (stateReadThrows) headState.MissingNodeBlock = payload.BlockHash;

        // Attributes are attached so a failed evaluation is seen to cost no block proposal.
        ResultWrapper<ForkchoiceUpdatedV2Result> fcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(payload.BlockHash, parent.Hash!, parent.Hash!),
            BuildBogotaPayloadAttributes(inclusionList: [], timestamp: payload.Timestamp + 12, slotNumber: 2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fcu.Result.ResultType, Is.EqualTo(ResultType.Success), fcu.Result.Error);
            Assert.That(fcu.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(fcu.Data.PayloadStatus.InclusionListSatisfied, Is.EqualTo(stateReadThrows ? null : satisfied));
            Assert.That(fcu.Data.PayloadId, Is.Not.Null);
        }
    }

    // The inclusion list is a per-call parameter, not a property of the block, so the answer newPayloadV6
    // cached for one call must never stand in for a later call that supplied a different list.
    [TestCase(true, false, false, TestName = "ForkchoiceUpdatedV5_recomputes_a_resent_inclusion_list_once_the_head_state_is_readable")]
    [TestCase(false, false, null, TestName = "ForkchoiceUpdatedV5_reports_null_for_a_resent_inclusion_list_it_cannot_evaluate")]
    [TestCase(true, true, false, TestName = "ForkchoiceUpdatedV5_reports_censorship_proven_without_gas_dimensions")]
    public async Task ForkchoiceUpdatedV5_does_not_answer_a_resent_inclusion_list_from_the_previous_answer(
        bool stateHealed, bool dimensionsLost, bool? expected)
    {
        HeadStateInterceptor headState = new();
        using MergeTestBlockchain chain = await CreateBlockchainWithHeadState(headState,
            new MergeConfig { TerminalTotalDifficulty = "0", NewPayloadCacheSize = 0 });
        IEngineRpcModule rpc = chain.EngineRpcModule;

        // An empty list is trivially satisfied, so this leaves inclusionListSatisfied=true cached for the head.
        ExecutionPayloadV4 payload = await BuildAndInsertEmptyBlock(rpc, chain.BlockTree.HeadHash, slot: 2);

        await RetainInclusionListByPrunedResend(rpc, headState, payload, [Rlp.Encode(BuildInclusionListTransfer()).Bytes]);
        if (!stateHealed) headState.PrunedBlock = payload.BlockHash;
        if (dimensionsLost)
        {
            Block executed = chain.BlockTree.FindBlock(payload.BlockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded)!;
            Assert.That(executed.Header.GasUsedPerDimension, Is.Not.Null);
            executed.Header.GasUsedPerDimension = null;
            chain.BlockTree.FindHeader(payload.BlockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded)!.GasUsedPerDimension = null;
            Assert.That(chain.StateReader.HasStateForBlock(executed.Header), Is.True,
                "readable state alone cannot recover EIP-8037 gas dimensions");
        }

        ResultWrapper<ForkchoiceUpdatedV2Result> fcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(payload.BlockHash, payload.BlockHash, payload.BlockHash), payloadAttributes: null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fcu.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(fcu.Data.PayloadStatus.InclusionListSatisfied, Is.EqualTo(expected));
        }
    }

    [Test]
    public async Task ForkchoiceUpdatedV5_retains_an_unresolved_branch_tip_across_unrelated_payloads()
    {
        HeadStateInterceptor headState = new();
        using MergeTestBlockchain chain = await CreateBlockchainWithHeadState(headState);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 parentHash = chain.BlockTree.HeadHash;

        ExecutionPayloadV4 first = await BuildAndInsertEmptyBlock(rpc, parentHash, slot: 2, finalize: false);
        await RetainInclusionListByPrunedResend(rpc, headState, first, [Rlp.Encode(BuildInclusionListTransfer()).Bytes]);

        for (ulong slot = 3; slot < 21; slot++)
        {
            await BuildAndInsertEmptyBlock(rpc, parentHash, slot, finalize: false);
        }

        ResultWrapper<ForkchoiceUpdatedV2Result> fcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(first.BlockHash, parentHash, parentHash), payloadAttributes: null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fcu.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(fcu.Data.PayloadStatus.InclusionListSatisfied, Is.False);
        }
    }

    [Test]
    public async Task ForkchoiceUpdatedV5_answers_a_parent_head_whose_child_was_validated(
        [Values] bool parentAnswered, [Values(PayloadStatus.Valid, PayloadStatus.Accepted, PayloadStatus.Syncing)] string childStatus)
    {
        HeadStateInterceptor headState = new();
        StatusOverridingNewPayloadHandler newPayloadHandler = new();
        using MergeTestBlockchain chain = await CreateBlockchainWithHeadState(headState, configure: builder => builder
            .AddDecorator<IAsyncHandler<ExecutionPayload, PayloadStatusV1>>((_, inner) =>
            {
                newPayloadHandler.Inner = inner;
                return newPayloadHandler;
            }));
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 genesis = chain.BlockTree.HeadHash;

        ExecutionPayloadV4 parent = await BuildAndInsertEmptyBlock(rpc, genesis, slot: 2, finalize: false, finalizedHash: genesis);
        ExecutionPayloadV4 child = await BuildAndInsertEmptyBlock(rpc, parent.BlockHash, slot: 3, finalize: false, finalizedHash: genesis);
        byte[][] inclusionList = [Rlp.Encode(BuildInclusionListTransfer()).Bytes];

        if (parentAnswered)
        {
            ResultWrapper<PayloadStatusV2> censored = await rpc.engine_newPayloadV6(parent, [], Keccak.Zero, [], inclusionList);
            Assert.That(censored.Data.InclusionListSatisfied, Is.False);
        }
        else
        {
            await RetainInclusionListByPrunedResend(rpc, headState, parent, inclusionList);
        }

        if (childStatus == PayloadStatus.Syncing)
        {
            await RetainInclusionListByPrunedResend(rpc, headState, child, inclusionList);
            ResultWrapper<ForkchoiceUpdatedV2Result> childFcu = await rpc.engine_forkchoiceUpdatedV5(
                new ForkchoiceStateV1(child.BlockHash, genesis, genesis), payloadAttributes: null);
            Assert.That(childFcu.Data.PayloadStatus.InclusionListSatisfied, Is.False);
        }
        else
        {
            newPayloadHandler.Status = childStatus == PayloadStatus.Accepted ? childStatus : null;
            ResultWrapper<PayloadStatusV2> childResend = await rpc.engine_newPayloadV6(child, [], Keccak.Zero, [], inclusionList);
            newPayloadHandler.Status = null;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(childResend.Data.Status, Is.EqualTo(childStatus));
                Assert.That(childResend.Data.InclusionListSatisfied, Is.EqualTo(childStatus == PayloadStatus.Valid ? false : null));
            }
        }

        ResultWrapper<ForkchoiceUpdatedV2Result> fcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(parent.BlockHash, genesis, genesis), payloadAttributes: null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fcu.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(fcu.Data.PayloadStatus.InclusionListSatisfied, Is.False);
        }
    }

    [Test]
    public async Task ForkchoiceUpdatedV5_prunes_earlier_syncing_lists_when_a_descendant_is_finalized()
    {
        HeadStateInterceptor headState = new();
        using MergeTestBlockchain chain = await CreateBlockchainWithHeadState(headState);
        EngineRpcModule rpc = (EngineRpcModule)chain.EngineRpcModule;
        Hash256 genesis = chain.BlockTree.HeadHash;

        ExecutionPayloadV4 first = await BuildAndInsertEmptyBlock(rpc, genesis, slot: 2, finalize: false, finalizedHash: genesis);
        ExecutionPayloadV4 second = await BuildAndInsertEmptyBlock(rpc, first.BlockHash, slot: 3, finalize: false, finalizedHash: genesis);
        ExecutionPayloadV4 third = await BuildAndInsertEmptyBlock(rpc, second.BlockHash, slot: 4, finalize: false, finalizedHash: genesis);
        ExecutionPayloadV4 sibling = await BuildAndInsertEmptyBlock(rpc, second.BlockHash, slot: 5, finalize: false, finalizedHash: genesis);

        byte[][] inclusionList = [Rlp.Encode(BuildInclusionListTransfer()).Bytes];
        await RetainInclusionListByPrunedResend(rpc, headState, first, inclusionList);
        await RetainInclusionListByPrunedResend(rpc, headState, second, inclusionList);
        ResultWrapper<PayloadStatusV2> thirdResend = await rpc.engine_newPayloadV6(third, [], Keccak.Zero, [], []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(thirdResend.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(rpc.HasRetainedInclusionList(first.BlockHash), Is.True,
                "a VALID grandchild alone does not make the earlier SYNCING list disappear");
        }

        ResultWrapper<ForkchoiceUpdatedV2Result> syncingFcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(Keccak.Compute("unknown-head"), third.BlockHash, third.BlockHash), payloadAttributes: null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(syncingFcu.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Syncing));
            Assert.That(rpc.HasRetainedInclusionList(first.BlockHash), Is.False,
                "a SYNCING forkchoice update must still prune blocks behind its finalized marker");
        }

        ResultWrapper<ForkchoiceUpdatedV2Result> finalizing = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(third.BlockHash, third.BlockHash, third.BlockHash), payloadAttributes: null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(finalizing.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(rpc.HasRetainedInclusionList(first.BlockHash), Is.False);
            Assert.That(rpc.HasRetainedInclusionList(second.BlockHash), Is.False);
            Assert.That(rpc.HasInclusionListAnswer(third.BlockHash), Is.True);
            Assert.That(rpc.HasInclusionListAnswer(sibling.BlockHash), Is.False);
        }
    }

    [Test]
    public async Task Inclusion_list_cache_keeps_accepted_tips_without_finality()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance,
            new MergeConfig { TerminalTotalDifficulty = "0" });
        EngineRpcModule rpc = (EngineRpcModule)chain.EngineRpcModule;
        Hash256 first = Keccak.Compute(BitConverter.GetBytes(0));
        Hash256 newest = first;

        for (int i = 0; i < 300; i++)
        {
            newest = Keccak.Compute(BitConverter.GetBytes(i));
            rpc.SetRetainedInclusionList(newest, (ulong)i + 1, [new byte[1]], accepted: true);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rpc.RetainedInclusionListCount, Is.EqualTo(300));
            Assert.That(rpc.HasRetainedInclusionList(first), Is.True);
            Assert.That(rpc.HasRetainedInclusionList(newest), Is.True);
        }
    }

    [Test]
    public async Task Inclusion_list_cache_limits_syncing_entries_without_evicting_accepted_tips()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance,
            new MergeConfig { TerminalTotalDifficulty = "0" });
        EngineRpcModule rpc = (EngineRpcModule)chain.EngineRpcModule;
        Hash256 accepted = Keccak.Compute("accepted-tip");
        Hash256 oldestSyncing = Keccak.Compute(BitConverter.GetBytes(0));
        Hash256 newestSyncing = oldestSyncing;

        rpc.SetRetainedInclusionList(accepted, 1, [new byte[1]], accepted: true);
        for (int i = 0; i < 65; i++)
        {
            newestSyncing = Keccak.Compute(BitConverter.GetBytes(i));
            rpc.SetRetainedInclusionList(newestSyncing, 1, [new byte[1]], accepted: false);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rpc.RetainedInclusionListCount, Is.EqualTo(65));
            Assert.That(rpc.HasRetainedInclusionList(accepted), Is.True);
            Assert.That(rpc.HasRetainedInclusionList(oldestSyncing), Is.False);
            Assert.That(rpc.HasRetainedInclusionList(newestSyncing), Is.True);
        }
    }

    [Test]
    public async Task Inclusion_list_cache_limits_answers_without_finality()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance,
            new MergeConfig { TerminalTotalDifficulty = "0" });
        EngineRpcModule rpc = (EngineRpcModule)chain.EngineRpcModule;
        Hash256 oldest = Keccak.Compute(BitConverter.GetBytes(0));
        Hash256 newest = oldest;

        for (int i = 0; i < 257; i++)
        {
            newest = Keccak.Compute(BitConverter.GetBytes(i));
            rpc.SetInclusionListAnswer(newest, (ulong)i + 1, answer: true);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rpc.HasInclusionListAnswer(oldest), Is.False);
            Assert.That(rpc.HasInclusionListAnswer(newest), Is.True);
        }
    }

    // A newPayloadV6 landing while forkchoiceUpdatedV5 evaluates the retained list wins: the answer being
    // computed is already stale, so publishing it would shadow the newer list on every later update.
    [Test]
    public async Task ForkchoiceUpdatedV5_does_not_publish_an_answer_for_a_list_superseded_while_it_was_evaluated()
    {
        HeadStateInterceptor headState = new();
        InterleavingEvaluator evaluator = new();
        using MergeTestBlockchain chain = await CreateBlockchainWithInterleavingEvaluator(headState, evaluator);
        IEngineRpcModule rpc = chain.EngineRpcModule;

        ExecutionPayloadV4 payload = await BuildAndInsertEmptyBlock(rpc, chain.BlockTree.HeadHash, slot: 2);
        await RetainInclusionListByPrunedResend(rpc, headState, payload, [Rlp.Encode(BuildInclusionListTransfer()).Bytes]);

        // A nonce the sender is past cannot be appended, so this list is satisfied where the censoring one is not.
        Transaction unappendableTx = Build.A.Transaction
            .WithNonce(99).WithMaxFeePerGas(10.GWei).WithMaxPriorityFeePerGas(2.GWei).WithGasLimit(100_000)
            .WithTo(TestItem.AddressA).SignedAndResolved(TestItem.PrivateKeyB).TestObject;

        // Stand in for the concurrent call: retain the satisfied list after the forkchoice update has read the
        // censoring one but before it can publish an answer for it.
        evaluator.BeforeEvaluate = () =>
        {
            evaluator.BeforeEvaluate = null;
            RetainInclusionListByPrunedResend(rpc, headState, payload, [Rlp.Encode(unappendableTx).Bytes]).GetAwaiter().GetResult();
        };

        ForkchoiceStateV1 forkchoiceState = new(payload.BlockHash, payload.BlockHash, payload.BlockHash);
        ResultWrapper<ForkchoiceUpdatedV2Result> superseded = await rpc.engine_forkchoiceUpdatedV5(forkchoiceState, payloadAttributes: null);
        ResultWrapper<ForkchoiceUpdatedV2Result> next = await rpc.engine_forkchoiceUpdatedV5(forkchoiceState, payloadAttributes: null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(superseded.Data.PayloadStatus.InclusionListSatisfied, Is.False, "answer for the list that update read");
            Assert.That(next.Data.PayloadStatus.InclusionListSatisfied, Is.True, "answer for the list that superseded it");
        }
    }

    // bogota.md lets a retained list go only once its payload is no longer a tip, so an evaluation that
    // threw answers null for that call alone and a later update still gets to answer.
    [Test]
    public async Task ForkchoiceUpdatedV5_re_evaluates_a_retained_list_after_an_evaluation_threw()
    {
        HeadStateInterceptor headState = new();
        InterleavingEvaluator evaluator = new();
        using MergeTestBlockchain chain = await CreateBlockchainWithInterleavingEvaluator(headState, evaluator);
        IEngineRpcModule rpc = chain.EngineRpcModule;

        ExecutionPayloadV4 payload = await BuildAndInsertEmptyBlock(rpc, chain.BlockTree.HeadHash, slot: 2);
        await RetainInclusionListByPrunedResend(rpc, headState, payload, [Rlp.Encode(BuildInclusionListTransfer()).Bytes]);

        int evaluations = 0;
        evaluator.BeforeEvaluate = () =>
        {
            if (++evaluations == 1) throw new InvalidOperationException("evaluation failed");
        };

        ForkchoiceStateV1 forkchoiceState = new(payload.BlockHash, payload.BlockHash, payload.BlockHash);
        ResultWrapper<ForkchoiceUpdatedV2Result> first = await rpc.engine_forkchoiceUpdatedV5(forkchoiceState, payloadAttributes: null);
        ResultWrapper<ForkchoiceUpdatedV2Result> second = await rpc.engine_forkchoiceUpdatedV5(forkchoiceState, payloadAttributes: null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(first.Data.PayloadStatus.InclusionListSatisfied, Is.Null, "the failed evaluation cannot answer");
            Assert.That(second.Data.PayloadStatus.InclusionListSatisfied, Is.False);
            Assert.That(evaluations, Is.EqualTo(2), "the retained list must survive an evaluation that threw");
        }
    }

    /// <summary>Builds a Bogota chain whose <see cref="NewPayloadHandler"/> reads state through <paramref name="headState"/>.</summary>
    private async Task<MergeTestBlockchain> CreateBlockchainWithHeadState(HeadStateInterceptor headState,
        MergeConfig? mergeConfig = null, Action<ContainerBuilder>? configure = null)
    {
        MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, mergeConfig ?? new MergeConfig { TerminalTotalDifficulty = "0" },
            configurer: builder =>
            {
                builder.UpdateSingleton<NewPayloadHandler>(inner => inner.AddSingleton<IStateReader>(headState));
                configure?.Invoke(builder);
            });
        headState.Inner = chain.StateReader;
        return chain;
    }

    private Task<MergeTestBlockchain> CreateBlockchainWithInterleavingEvaluator(HeadStateInterceptor headState, InterleavingEvaluator evaluator) =>
        CreateBlockchainWithHeadState(headState, configure: builder => builder
            .AddDecorator<IInclusionListComplianceEvaluator>((_, inner) =>
            {
                evaluator.Inner = inner;
                return evaluator;
            }));

    /// <summary>Resends <paramref name="payload"/> with its state hidden, so newPayloadV6 answers <c>SYNCING</c>
    /// and <paramref name="inclusionList"/> is left retained for the forkchoice update.</summary>
    private static async Task RetainInclusionListByPrunedResend(IEngineRpcModule rpc, HeadStateInterceptor headState,
        ExecutionPayloadV4 payload, byte[][] inclusionList)
    {
        headState.PrunedBlock = payload.BlockHash;
        ResultWrapper<PayloadStatusV2> resend = await rpc.engine_newPayloadV6(payload, [], Keccak.Zero, [], inclusionList);
        headState.PrunedBlock = null;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resend.Data.Status, Is.EqualTo(PayloadStatus.Syncing), "the resend must leave its list retained");
            Assert.That(resend.Data.InclusionListSatisfied, Is.Null);
        }
    }

    /// <summary>Occupies the block processor so the payload sent next has to queue and its newPayload times out.</summary>
    private static void OccupyBlockProcessor(MergeTestBlockchain chain, Block parent, Task processingRelease)
    {
        using ManualResetEventSlim processingStarted = new(false);
        TestBranchProcessorInterceptor branchProcessor = (TestBranchProcessorInterceptor)chain.BranchProcessor;
        branchProcessor.ProcessingStarted = processingStarted;
        branchProcessor.ProcessingRelease = processingRelease;
        Block occupyBlock = Build.A.Block.WithNumber(parent.Number + 1).WithParent(parent)
            .WithNonce(0).WithDifficulty(0).WithStateRoot(parent.StateRoot!).TestObject;
        occupyBlock.Header.TotalDifficulty = parent.TotalDifficulty;
        try
        {
            _ = Task.Run(async () => await chain.BlockProcessingQueue.Enqueue(
                occupyBlock, ProcessingOptions.ForceProcessing | ProcessingOptions.DoNotUpdateHead));
            Assert.That(processingStarted.Wait(TimeSpan.FromSeconds(5)), Is.True, "the block processor was never occupied");
        }
        finally
        {
            // Later blocks must not signal an event disposed here while the chain is still processing.
            branchProcessor.ProcessingStarted = null;
        }
    }

    /// <summary>A transfer that fits an empty Bogota payload, so a list holding it is unsatisfied unless the block includes it.</summary>
    private static Transaction BuildInclusionListTransfer() => Build.A.Transaction
        .WithNonce(0).WithMaxFeePerGas(10.GWei).WithMaxPriorityFeePerGas(2.GWei).WithGasLimit(100_000)
        .WithTo(TestItem.AddressA).SignedAndResolved(TestItem.PrivateKeyB).TestObject;

    /// <summary>Forces a chosen status onto <c>newPayload</c> results after the wrapped handler has run.</summary>
    private sealed class StatusOverridingNewPayloadHandler : IAsyncHandler<ExecutionPayload, PayloadStatusV1>
    {
        public IAsyncHandler<ExecutionPayload, PayloadStatusV1> Inner { get; set; } = null!;

        /// <summary>Status to answer with, or <c>null</c> to pass the wrapped handler's result through.</summary>
        public string? Status { get; set; }

        public async Task<ResultWrapper<PayloadStatusV1>> HandleAsync(ExecutionPayload request)
        {
            ResultWrapper<PayloadStatusV1> result = await Inner.HandleAsync(request);
            return Status is { } status ? ResultWrapper<PayloadStatusV1>.Success(new PayloadStatusV1 { Status = status }) : result;
        }
    }

    /// <summary>Wraps the chain's <see cref="IInclusionListComplianceEvaluator"/> so a test can act inside the
    /// window between <c>engine_forkchoiceUpdatedV5</c> reading the retained list and publishing an answer.</summary>
    private sealed class InterleavingEvaluator : IInclusionListComplianceEvaluator
    {
        public IInclusionListComplianceEvaluator Inner { get; set; } = null!;

        public Action? BeforeEvaluate { get; set; }

        public bool? TryEvaluate(Hash256 blockHash, byte[][] inclusionListTransactions)
        {
            BeforeEvaluate?.Invoke();
            return Inner.TryEvaluate(blockHash, inclusionListTransactions);
        }
    }

    /// <summary>Wraps the chain's <see cref="IStateReader"/> so one block's state can be made unreadable.</summary>
    /// <remarks>
    /// <see cref="Inner"/> is assigned once the chain is built, because the reader being wrapped is itself one
    /// of the chain's components; nothing reads state during the build.
    /// </remarks>
    private sealed class HeadStateInterceptor : IStateReader
    {
        public IStateReader Inner { get; set; } = null!;

        /// <summary>Block whose state must appear pruned, as it is once the block leaves the pruning window.</summary>
        public Hash256? PrunedBlock { get; set; }

        /// <summary>Block whose account reads must throw, as they do when a subtrie node is absent under a present root.</summary>
        public Hash256? MissingNodeBlock { get; set; }

        public bool HasStateForBlock(BlockHeader? baseBlock) => !Matches(baseBlock, PrunedBlock) && Inner.HasStateForBlock(baseBlock);

        public bool TryGetAccount(BlockHeader? baseBlock, Address address, out AccountStruct account) =>
            Matches(baseBlock, MissingNodeBlock)
                ? throw new MissingTrieNodeException("Node missing", null, TreePath.Empty, Keccak.Zero)
                : Inner.TryGetAccount(baseBlock, address, out account);

        public void GetStorage(BlockHeader? baseBlock, Address address, in UInt256 index, out UInt256 value) =>
            Inner.GetStorage(baseBlock, address, in index, out value);

        public byte[]? GetCode(Hash256 codeHash) => Inner.GetCode(codeHash);

        public byte[]? GetCode(in ValueHash256 codeHash) => Inner.GetCode(in codeHash);

        public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, BlockHeader? baseBlock, VisitingOptions? visitingOptions = null, VisitingStats? diagnostics = null)
            where TCtx : struct, INodeContext<TCtx> => Inner.RunTreeVisitor(treeVisitor, baseBlock, visitingOptions, diagnostics);

        private static bool Matches(BlockHeader? header, Hash256? hash) => hash is not null && header?.Hash == hash;
    }

    // The re-check must judge the resent list on the dimensions execution recorded, not on the max(execution,
    // state) the header reduces them to. The two differ only above the EIP-7825 execution cap: past it a
    // transaction reserves the cap on the execution dimension but its whole gas on the state one, so an entry
    // that fits both dimensions reads as unappendable against the max, and real censorship goes unreported.
    // After cache loss, bounds still prove some verdicts. Only ambiguous cases answer SYNCING with null
    // compliance (bogota.md newPayloadV6 (2.2)); recovering their exact answer needs safe re-execution.
    [TestCase(50, false, InclusionListEntry.Boundary, false, TestName = "NewPayloadV6_re_judges_a_resent_block_on_the_recorded_gas_dimensions")]
    [TestCase(1, false, InclusionListEntry.Boundary, false, TestName = "NewPayloadV6_re_judges_a_resent_block_on_the_recorded_gas_dimensions_after_the_payload_cache_evicts_it")]
    [TestCase(1, true, InclusionListEntry.Boundary, false, TestName = "NewPayloadV6_declines_to_judge_a_resent_block_once_both_caches_lose_the_gas_dimensions")]
    [TestCase(1, true, InclusionListEntry.Included, false, TestName = "NewPayloadV6_answers_an_included_list_after_both_dimension_caches_are_lost")]
    [TestCase(1, true, InclusionListEntry.Boundary, true, TestName = "NewPayloadV6_declines_an_omitted_list_for_the_head_after_both_dimension_caches_are_lost")]
    [TestCase(1, true, InclusionListEntry.Included, true, TestName = "NewPayloadV6_answers_an_included_list_for_the_head_after_both_dimension_caches_are_lost")]
    [TestCase(1, true, InclusionListEntry.Small, false, TestName = "NewPayloadV6_reports_censorship_proven_without_gas_dimensions")]
    [TestCase(1, true, InclusionListEntry.Small, true, TestName = "NewPayloadV6_reports_censorship_for_the_head_without_gas_dimensions")]
    [TestCase(1, true, InclusionListEntry.WrongNonce, false, TestName = "NewPayloadV6_answers_an_unappendable_list_without_gas_dimensions")]
    [TestCase(1, true, InclusionListEntry.WrongNonce, true, TestName = "NewPayloadV6_answers_an_unappendable_list_for_the_head_without_gas_dimensions")]
    public async Task NewPayloadV6_re_judges_a_resent_block_on_the_recorded_gas_dimensions(
        int newPayloadCacheSize, bool dimensionsLost, InclusionListEntry entry, bool resendHead)
    {
        // Genesis is raised to the production target so the block's remaining gas exceeds the execution cap;
        // the 4M default leaves no room for a transaction big enough to tell the two rules apart.
        const ulong gasLimit = 30_000_000UL;
        AccountReadCountingStateReader accountReader = null!;
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance,
            new MergeConfig { TerminalTotalDifficulty = "0", NewPayloadCacheSize = newPayloadCacheSize },
            configurer: builder => builder
                .WithGenesisPostProcessor((genesis, _) => genesis.Header.GasLimit = gasLimit)
                .AddDecorator<IStateReader>((_, reader) =>
                    accountReader = new AccountReadCountingStateReader(reader, TestItem.AddressC)));
        IEngineRpcModule rpc = chain.EngineRpcModule;

        // A transaction of its own, so the block's execution dimension outgrows its state dimension.
        Transaction included = Build.A.Transaction
            .WithNonce(0).WithMaxFeePerGas(10.GWei).WithMaxPriorityFeePerGas(2.GWei)
            .WithTo(TestItem.AddressA).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        byte[][] firstList = [Rlp.Encode(included).Bytes];

        ResultWrapper<ForkchoiceUpdatedV2Result> fcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(chain.BlockTree.HeadHash, Keccak.Zero, chain.BlockTree.HeadHash),
            BuildBogotaPayloadAttributes(firstList, targetGasLimit: gasLimit, timestamp: Timestamper.UnixTime.Seconds + 2, slotNumber: 2));
        ResultWrapper<GetPayloadV6Result?> payloadResult = await rpc.engine_getPayloadV6(Bytes.FromHexString(fcu.Data.PayloadId!));
        ExecutionPayloadV4 first = payloadResult.Data!.ExecutionPayload;

        await rpc.engine_newPayloadV6(first, [], Keccak.Zero, payloadResult.Data!.ExecutionRequests, firstList);
        await BuildAndInsertEmptyBlock(rpc, first.BlockHash, slot: 3, finalize: !resendHead);

        Block executed = chain.BlockTree.FindBlock(first.BlockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded)!;
        Assert.That(executed.Header.GasUsedPerDimension, Is.Not.Null, "the dimensions must reach the block a re-check is handed");
        (ulong execution, ulong state) = executed.Header.GasUsedPerDimension!.Value;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(executed.Transactions, Has.Length.EqualTo(1));
            Assert.That(Math.Max(execution, state), Is.EqualTo(executed.GasUsed), "EIP-8037 reduces the dimensions to their maximum");
            Assert.That(execution, Is.GreaterThan(state), "the max must exceed the state dimension for the two rules to differ");
        }

        // Reserves the whole remaining state dimension, and only the execution cap on the execution dimension:
        // appendable on the recorded dimensions, over budget against max(execution, state).
        Transaction censored = Build.A.Transaction
            .WithNonce(entry == InclusionListEntry.WrongNonce ? 99UL : 0UL)
            .WithGasLimit(entry is InclusionListEntry.Small or InclusionListEntry.WrongNonce ? 100_000UL : gasLimit - state).WithMaxFeePerGas(10.GWei).WithMaxPriorityFeePerGas(2.GWei)
            .WithTo(TestItem.AddressA).SignedAndResolved(TestItem.PrivateKeyC).TestObject;
        if (entry == InclusionListEntry.Boundary)
            Assert.That(censored.GasLimit, Is.GreaterThan(gasLimit - execution), "the entry must not fit the max");

        // What a restart leaves behind: the payload cache is already past this block at size 1, and a header
        // read back from disk carries no dimensions. Neither an ancestor nor the head itself can be
        // re-executed to recover them here.
        if (resendHead)
        {
            await rpc.engine_forkchoiceUpdatedV5(
                new ForkchoiceStateV1(first.BlockHash, first.BlockHash, first.BlockHash), payloadAttributes: null);
            Assert.That(chain.BlockTree.HeadHash, Is.EqualTo(first.BlockHash), "the resend must target the current head");
        }

        if (dimensionsLost)
        {
            executed.Header.GasUsedPerDimension = null;
            chain.BlockTree.FindHeader(first.BlockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded)!.GasUsedPerDimension = null;
        }

        accountReader.TrackedBlockHash = first.BlockHash;

        ResultWrapper<PayloadStatusV2> resend = await rpc.engine_newPayloadV6(
            first, [], Keccak.Zero, [], entry == InclusionListEntry.Included ? firstList : [Rlp.Encode(censored).Bytes]);

        if (dimensionsLost && resendHead && entry == InclusionListEntry.Boundary)
        {
            EngineRpcModule engineRpc = (EngineRpcModule)rpc;
            Assert.That(engineRpc.HasRetainedInclusionList(first.BlockHash), Is.True);
            Transaction[]? sharedList = executed.InclusionListTransactions;
            ResultWrapper<ForkchoiceUpdatedV2Result> retained = await rpc.engine_forkchoiceUpdatedV5(
                new ForkchoiceStateV1(first.BlockHash, first.BlockHash, first.BlockHash), payloadAttributes: null);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(retained.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
                Assert.That(retained.Data.PayloadStatus.InclusionListSatisfied, Is.Null,
                    "the retained list remains ambiguous across gas-dimension bounds");
                Assert.That(executed.Header.GasUsedPerDimension, Is.Null, "bounds must not alter the tree's header");
                Assert.That(executed.InclusionListTransactions, Is.SameAs(sharedList), "the retained list must not replace the tree's list");
            }
        }

        bool answerUnavailable = dimensionsLost && entry == InclusionListEntry.Boundary;
        bool expectedSatisfied = entry is InclusionListEntry.Included or InclusionListEntry.WrongNonce;
        using (Assert.EnterMultipleScope())
        {
            if (dimensionsLost && entry is InclusionListEntry.WrongNonce)
                Assert.That(accountReader.AccountReads, Is.EqualTo(1), "all gas-bound passes must share the sender account read");
            Assert.That(resend.Data.Status, Is.EqualTo(answerUnavailable ? PayloadStatus.Syncing : PayloadStatus.Valid));
            Assert.That(resend.Data.InclusionListSatisfied, answerUnavailable ? Is.Null : Is.EqualTo(expectedSatisfied),
                answerUnavailable ? "without the dimensions the answer must be withheld, not guessed"
                    : expectedSatisfied ? "the entry is included or cannot be appended"
                    : "the omitted entry fits the remaining gas dimensions");
        }
    }

    /// <summary>Entries exercising known and ambiguous compliance after gas-dimension cache loss.</summary>
    public enum InclusionListEntry { Boundary, Included, Small, WrongNonce }

    /// <summary>Counts a selected sender's account reads during one payload resend.</summary>
    private sealed class AccountReadCountingStateReader(IStateReader inner, Address sender) : IStateReader
    {
        private int _accountReads;
        public Hash256? TrackedBlockHash { get; set; }
        public int AccountReads => Volatile.Read(ref _accountReads);

        public bool TryGetAccount(BlockHeader? baseBlock, Address address, out AccountStruct account)
        {
            if (TrackedBlockHash is { } hash && baseBlock?.Hash == hash && address == sender)
                Interlocked.Increment(ref _accountReads);
            return inner.TryGetAccount(baseBlock, address, out account);
        }

        public bool HasStateForBlock(BlockHeader? baseBlock) => inner.HasStateForBlock(baseBlock);
        public void GetStorage(BlockHeader? baseBlock, Address address, in UInt256 index, out UInt256 value) =>
            inner.GetStorage(baseBlock, address, in index, out value);
        public byte[]? GetCode(Hash256 codeHash) => inner.GetCode(codeHash);
        public byte[]? GetCode(in ValueHash256 codeHash) => inner.GetCode(in codeHash);
        public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, BlockHeader? baseBlock, VisitingOptions? visitingOptions = null, VisitingStats? diagnostics = null)
            where TCtx : struct, INodeContext<TCtx> => inner.RunTreeVisitor(treeVisitor, baseBlock, visitingOptions, diagnostics);
    }

    private async Task<ExecutionPayloadV4> BuildAndInsertEmptyBlock(IEngineRpcModule rpc, Hash256 parent, ulong slot,
        bool finalize = true, Hash256? finalizedHash = null)
    {
        ResultWrapper<ForkchoiceUpdatedV2Result> fcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(parent, Keccak.Zero, finalizedHash ?? parent),
            BuildBogotaPayloadAttributes(inclusionList: [], timestamp: Timestamper.UnixTime.Seconds + slot, slotNumber: slot));
        ResultWrapper<GetPayloadV6Result?> payloadResult = await rpc.engine_getPayloadV6(Bytes.FromHexString(fcu.Data.PayloadId!));
        ExecutionPayloadV4 payload = payloadResult.Data!.ExecutionPayload;

        await rpc.engine_newPayloadV6(payload, [], Keccak.Zero, payloadResult.Data!.ExecutionRequests, []);
        Hash256 checkpoint = finalize ? payload.BlockHash : finalizedHash ?? parent;
        await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(payload.BlockHash, checkpoint, checkpoint), payloadAttributes: null);
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
