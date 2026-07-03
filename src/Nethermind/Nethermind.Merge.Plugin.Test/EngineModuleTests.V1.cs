// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Crypto;
using Nethermind.Facade.Eth;
using Nethermind.HealthChecks;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Test;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.SszRest.Handlers;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [TestCase(
        "0xb1b3b07ef3832bd409a04fdea9bf2bfa83d7af0f537ff25f4a3d2eb632ebfb0f",
        "0x1c53bdbf457025f80c6971a9cf50986974eed02f0a9acaeeb49cafef10efd133",
        "0x5adf9b330b6c3fe0")]
    public virtual async Task processing_block_should_serialize_valid_responses(string blockHash, string latestValidHash, string payloadId)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(null, new MergeConfig()
        {
            TerminalTotalDifficulty = "0"
        });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;
        Hash256 prevRandao = Keccak.Zero;
        Address feeRecipient = TestItem.AddressC;
        UInt256 timestamp = Timestamper.UnixTime.Seconds;
        var forkChoiceUpdatedParams = new
        {
            headBlockHash = startingHead.ToString(),
            safeBlockHash = startingHead.ToString(),
            finalizedBlockHash = Keccak.Zero.ToString(),
        };
        var preparePayloadParams = new
        {
            timestamp = timestamp.ToHexString(true),
            prevRandao = prevRandao.ToString(),
            suggestedFeeRecipient = feeRecipient.ToString(),
        };
        string?[] parameters =
        {
            JsonSerializer.Serialize(forkChoiceUpdatedParams),
            JsonSerializer.Serialize(preparePayloadParams)
        };
        // prepare a payload
        string result = await RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV1", parameters!);
        byte[] expectedPayloadId = Bytes.FromHexString(payloadId);
        Assert.That(result, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":{{\"payloadStatus\":{{\"status\":\"VALID\",\"latestValidHash\":\"{latestValidHash}\",\"validationError\":null}},\"payloadId\":\"{expectedPayloadId.ToHexString(true)}\"}},\"id\":67}}"));

        Hash256 expectedBlockHash = new(blockHash);
        string? expectedPayload = chain.JsonSerializer.Serialize(new ExecutionPayload
        {
            BaseFeePerGas = 0,
            BlockHash = expectedBlockHash,
            BlockNumber = 1,
            ExtraData = Bytes.FromHexString("0x4e65746865726d696e64"), // Nethermind
            FeeRecipient = feeRecipient,
            GasLimit = chain.BlockTree.Head!.GasLimit,
            GasUsed = 0,
            LogsBloom = Bloom.Empty,
            ParentHash = startingHead,
            PrevRandao = prevRandao,
            ReceiptsRoot = chain.BlockTree.Head!.ReceiptsRoot!,
            StateRoot = chain.BlockTree.Head!.StateRoot!,
            Timestamp = timestamp.ToUInt64(null),
            Transactions = []
        });
        // get the payload
        result = await RpcTest.TestSerializedRequest(rpc, "engine_getPayloadV1", expectedPayloadId.ToHexString(true));
        Assert.That(result, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":{expectedPayload},\"id\":67}}"));
        // execute the payload
        result = await RpcTest.TestSerializedRequest(rpc, "engine_newPayloadV1", expectedPayload);
        Assert.That(result, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":{{\"status\":\"VALID\",\"latestValidHash\":\"{expectedBlockHash}\",\"validationError\":null}},\"id\":67}}"));

        forkChoiceUpdatedParams = new
        {
            headBlockHash = expectedBlockHash.ToString(true),
            safeBlockHash = expectedBlockHash.ToString(true),
            finalizedBlockHash = startingHead.ToString(true),
        };
        parameters = new[] { JsonSerializer.Serialize(forkChoiceUpdatedParams), null };
        // update the fork choice
        result = await RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV1", parameters!);
        Assert.That(result, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":{\"payloadStatus\":{\"status\":\"VALID\",\"latestValidHash\":\"" +
                           expectedBlockHash +
                           "\",\"validationError\":null},\"payloadId\":null},\"id\":67}"));
    }

    [Test]
    public async Task can_parse_forkchoiceUpdated_with_implicit_null_payloadAttributes()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        var forkChoiceUpdatedParams = new
        {
            headBlockHash = Keccak.Zero.ToString(),
            safeBlockHash = Keccak.Zero.ToString(),
            finalizedBlockHash = Keccak.Zero.ToString(),
        };
        string[] parameters = new[] { JsonSerializer.Serialize(forkChoiceUpdatedParams) };
        string? result = await RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV1", parameters);
        Assert.That(result, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":{\"payloadStatus\":{\"status\":\"SYNCING\",\"latestValidHash\":null,\"validationError\":null},\"payloadId\":null},\"id\":67}"));
    }

    [Test]
    public void ForkchoiceV1_ToString_returns_correct_results()
    {
        ForkchoiceStateV1 forkchoiceState = new(TestItem.KeccakA, TestItem.KeccakF, TestItem.KeccakC);
        Assert.That(forkchoiceState.ToString(), Is.EqualTo("ForkChoice: 0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760, Safe: 0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72, Finalized: 0xe61d9a3d3848fb2cdd9a2ab61e2f21a10ea431275aed628a0557f9dee697c37a"));
    }

    [Test]
    public void ForkchoiceV1_ToString_with_block_numbers_returns_correct_results()
    {
        ForkchoiceStateV1 forkchoiceState = new(TestItem.KeccakA, TestItem.KeccakF, TestItem.KeccakC);
        Assert.That(forkchoiceState.ToString(1, 2, 3), Is.EqualTo("ForkChoice: 1 (0x03783f...35b760), Safe: 2 (0x017e66...b18f72), Finalized: 3 (0xe61d9a...97c37a)"));
    }

    [Test]
    public async Task engine_forkchoiceUpdatedV1_with_payload_attributes_should_create_block_on_top_of_genesis_and_not_change_head()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;
        ulong timestamp = 30;
        Hash256 random = Keccak.Zero;
        Address feeRecipient = TestItem.AddressD;

        ExecutionPayload? executionPayloadV1 = await BuildAndGetPayloadResult(rpc, chain, startingHead,
            Keccak.Zero, startingHead, timestamp, random, feeRecipient);

        ExecutionPayload expected = CreateParentBlockRequestOnHead(chain.BlockTree);
        expected.GasLimit = 4000000L;
        expected.BlockHash = ExpectedBlockHash;
        expected.LogsBloom = Bloom.Empty;
        expected.FeeRecipient = feeRecipient;
        expected.BlockNumber = 1;
        expected.PrevRandao = random;
        expected.ParentHash = startingHead;
        expected.SetTransactions([]);
        expected.Timestamp = timestamp;
        expected.PrevRandao = random;
        expected.ExtraData = Encoding.UTF8.GetBytes("Nethermind");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(JToken.Parse(chain.JsonSerializer.Serialize(executionPayloadV1)), Is.EqualTo(JToken.Parse(chain.JsonSerializer.Serialize(expected))).Using(JToken.EqualityComparer));
            Hash256 actualHead = chain.BlockTree.HeadHash;
            Assert.That(actualHead, Is.Not.EqualTo(expected.BlockHash));
            Assert.That(actualHead, Is.EqualTo(startingHead));
        }
    }

    protected virtual Hash256 ExpectedBlockHash => new("0x3accc4186d73f4826acf1a8da3f7c696f16c3863e4f76b1315d65daa88fe28ff");

    [Test]
    public async Task forkchoiceUpdatedV1_should_not_create_block_or_change_head_with_unknown_parent()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;
        Hash256 notExistingHash = TestItem.KeccakH;
        ulong timestamp = Timestamper.UnixTime.Seconds;
        Hash256 random = Keccak.Zero;
        Address feeRecipient = Address.Zero;

        ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedV1Response = await rpc.engine_forkchoiceUpdatedV1(
            new ForkchoiceStateV1(notExistingHash, Keccak.Zero, notExistingHash),
            new PayloadAttributes { Timestamp = timestamp, SuggestedFeeRecipient = feeRecipient, PrevRandao = random });

        Assert.That(forkchoiceUpdatedV1Response.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Syncing)); // ToDo wait for final PostMerge sync
        byte[] payloadId = Bytes.FromHexString("0x5d071947bfcc3e65");
        ResultWrapper<ExecutionPayload?> getResponse = await rpc.engine_getPayloadV1(payloadId);

        Assert.That(getResponse.ErrorCode, Is.EqualTo(MergeErrorCodes.UnknownPayload));
        Hash256 actualHead = chain.BlockTree.HeadHash;
        Assert.That(actualHead, Is.Not.EqualTo(notExistingHash));
        Assert.That(actualHead, Is.EqualTo(startingHead));
    }

    [Test]
    public async Task executePayloadV1_accepts_previously_assembled_block_multiple_times([Values(1, 3)] int times)
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;
        BlockHeader startingBestSuggestedHeader = chain.BlockTree.BestSuggestedHeader!;
        ExecutionPayload getPayloadResult = await BuildAndGetPayloadResult(chain, rpc);
        Assert.That(getPayloadResult.ParentHash, Is.EqualTo(startingHead));


        for (int i = 0; i < times; i++)
        {
            ResultWrapper<PayloadStatusV1> executePayloadResult = await rpc.engine_newPayloadV1(getPayloadResult);
            Assert.That(executePayloadResult.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        }

        Hash256 bestSuggestedHeaderHash = chain.BlockTree.BestSuggestedHeader!.Hash!;
        Assert.That(bestSuggestedHeaderHash, Is.EqualTo(getPayloadResult.BlockHash));
        Assert.That(bestSuggestedHeaderHash, Is.Not.EqualTo(startingBestSuggestedHeader!.Hash!));
    }

    [Test]
    public async Task executePayloadV1_accepts_previously_prepared_block_multiple_times([Values(1, 3)] int times)
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;
        BlockHeader startingBestSuggestedHeader = chain.BlockTree.BestSuggestedHeader!;
        ExecutionPayload getPayloadResult = await PrepareAndGetPayloadResultV1(chain, rpc);
        Assert.That(getPayloadResult.ParentHash, Is.EqualTo(startingHead));


        for (int i = 0; i < times; i++)
        {
            ResultWrapper<PayloadStatusV1>? executePayloadResult = await rpc.engine_newPayloadV1(getPayloadResult);
            Assert.That(executePayloadResult.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        }

        Hash256 bestSuggestedHeaderHash = chain.BlockTree.BestSuggestedHeader!.Hash!;
        Assert.That(bestSuggestedHeaderHash, Is.EqualTo(getPayloadResult.BlockHash));
        Assert.That(bestSuggestedHeaderHash, Is.Not.EqualTo(startingBestSuggestedHeader!.Hash!));
    }

    [Test]
    public async Task block_should_not_be_canonical_before_forkchoiceUpdatedV1()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;

        ExecutionPayload getPayloadResult = await BuildAndGetPayloadResult(chain, rpc);
        Hash256 newHead = getPayloadResult.BlockHash!;

        await rpc.engine_newPayloadV1(getPayloadResult);
        Assert.That(chain.BlockTree.FindBlock(newHead, BlockTreeLookupOptions.RequireCanonical), Is.Null);
        Assert.That(chain.BlockTree.FindBlock(newHead, BlockTreeLookupOptions.None), Is.Not.Null);

        await rpc.engine_forkchoiceUpdatedV1(new ForkchoiceStateV1(newHead, Keccak.Zero, Keccak.Zero));
        Assert.That(chain.BlockTree.FindBlock(newHead, BlockTreeLookupOptions.RequireCanonical), Is.Not.Null);
        Assert.That(chain.BlockTree.FindBlock(newHead, BlockTreeLookupOptions.None), Is.Not.Null);
    }

    [Test]
    public async Task block_should_not_be_canonical_after_reorg()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;
        Hash256 finalizedHash = Keccak.Zero;
        ulong timestamp = 30;
        Hash256 random = Keccak.Zero;
        Address feeRecipientA = TestItem.AddressD;
        Address feeRecipientB = TestItem.AddressE;

        ExecutionPayload getPayloadResultA = await BuildAndGetPayloadResult(rpc, chain, startingHead,
            finalizedHash, startingHead, timestamp, random, feeRecipientA);
        Hash256 blochHashA = getPayloadResultA.BlockHash!;

        ExecutionPayload getPayloadResultB = await BuildAndGetPayloadResult(rpc, chain, startingHead,
            finalizedHash, startingHead, timestamp, random, feeRecipientB);
        Hash256 blochHashB = getPayloadResultB.BlockHash!;

        await rpc.engine_newPayloadV1(getPayloadResultA);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.BlockTree.FindBlock(blochHashA, BlockTreeLookupOptions.RequireCanonical), Is.Null);
            Assert.That(chain.BlockTree.FindBlock(blochHashB, BlockTreeLookupOptions.RequireCanonical), Is.Null);
            Assert.That(chain.BlockTree.FindBlock(blochHashA, BlockTreeLookupOptions.None), Is.Not.Null);
            Assert.That(chain.BlockTree.FindBlock(blochHashB, BlockTreeLookupOptions.None), Is.Null);
        }

        await rpc.engine_newPayloadV1(getPayloadResultB);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.BlockTree.FindBlock(blochHashA, BlockTreeLookupOptions.RequireCanonical), Is.Null);
            Assert.That(chain.BlockTree.FindBlock(blochHashB, BlockTreeLookupOptions.RequireCanonical), Is.Null);
            Assert.That(chain.BlockTree.FindBlock(blochHashA, BlockTreeLookupOptions.None), Is.Not.Null);
            Assert.That(chain.BlockTree.FindBlock(blochHashB, BlockTreeLookupOptions.None), Is.Not.Null);
        }

        await rpc.engine_forkchoiceUpdatedV1(new ForkchoiceStateV1(blochHashA, finalizedHash, startingHead));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.BlockTree.FindBlock(blochHashA, BlockTreeLookupOptions.RequireCanonical), Is.Not.Null);
            Assert.That(chain.BlockTree.FindBlock(blochHashB, BlockTreeLookupOptions.RequireCanonical), Is.Null);
            Assert.That(chain.BlockTree.FindBlock(blochHashA, BlockTreeLookupOptions.None), Is.Not.Null);
            Assert.That(chain.BlockTree.FindBlock(blochHashB, BlockTreeLookupOptions.None), Is.Not.Null);
        }

        await rpc.engine_forkchoiceUpdatedV1(new ForkchoiceStateV1(blochHashB, finalizedHash, startingHead));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.BlockTree.FindBlock(blochHashA, BlockTreeLookupOptions.RequireCanonical), Is.Null);
            Assert.That(chain.BlockTree.FindBlock(blochHashB, BlockTreeLookupOptions.RequireCanonical), Is.Not.Null);
            Assert.That(chain.BlockTree.FindBlock(blochHashA, BlockTreeLookupOptions.None), Is.Not.Null);
            Assert.That(chain.BlockTree.FindBlock(blochHashB, BlockTreeLookupOptions.None), Is.Not.Null);
        }

        await rpc.engine_forkchoiceUpdatedV1(new ForkchoiceStateV1(blochHashA, finalizedHash, startingHead));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.BlockTree.FindBlock(blochHashA, BlockTreeLookupOptions.RequireCanonical), Is.Not.Null);
            Assert.That(chain.BlockTree.FindBlock(blochHashB, BlockTreeLookupOptions.RequireCanonical), Is.Null);
            Assert.That(chain.BlockTree.FindBlock(blochHashA, BlockTreeLookupOptions.None), Is.Not.Null);
            Assert.That(chain.BlockTree.FindBlock(blochHashB, BlockTreeLookupOptions.None), Is.Not.Null);
        }
    }

    private async Task<ExecutionPayload> PrepareAndGetPayloadResultV1(MergeTestBlockchain chain,
        IEngineRpcModule rpc)
    {
        Hash256 startingHead = chain.BlockTree.HeadHash;
        ulong timestamp = Timestamper.UnixTime.Seconds;
        Hash256 random = Keccak.Zero;
        Address feeRecipient = Address.Zero;
        return await PrepareAndGetPayloadResultV1(rpc, startingHead, timestamp, random, feeRecipient);
    }

    private async Task<ExecutionPayload> PrepareAndGetPayloadResultV1(
        IEngineRpcModule rpc, Hash256 currentHead, ulong timestamp, Hash256 random, Address feeRecipient)
    {
        PayloadAttributes? payloadAttributes = new()
        {
            PrevRandao = random,
            SuggestedFeeRecipient = feeRecipient,
            Timestamp = timestamp
        };
        ForkchoiceStateV1? forkchoiceStateV1 = new(currentHead, currentHead, currentHead);
        ResultWrapper<ForkchoiceUpdatedV1Result>? forkchoiceUpdatedResult = await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1, payloadAttributes);
        byte[] payloadId = Bytes.FromHexString(forkchoiceUpdatedResult.Data.PayloadId!);
        ResultWrapper<ExecutionPayload?> getPayloadResult = await rpc.engine_getPayloadV1(payloadId);
        return getPayloadResult.Data!;
    }

    public static IEnumerable WrongInputTestsV1
    {
        get
        {
            yield return GetNewBlockRequestBadDataTestCase(static r => r.ReceiptsRoot, TestItem.KeccakD);
            yield return GetNewBlockRequestBadDataTestCase(static r => r.StateRoot, TestItem.KeccakD);

            Bloom bloom = new();
            bloom.Add([
                Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakG).TestObject
            ]);
            yield return GetNewBlockRequestBadDataTestCase(static r => r.LogsBloom, bloom);
            yield return GetNewBlockRequestBadDataTestCase(static r => r.Transactions, [[1]]);
            yield return GetNewBlockRequestBadDataTestCase(static r => r.GasUsed, 1UL);
        }
    }

    [Test]
    public async Task executePayloadV1_unknown_parentHash_return_syncing()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        ExecutionPayload getPayloadResult = await BuildAndGetPayloadResult(chain, rpc);
        Hash256 blockHash = getPayloadResult.BlockHash;
        getPayloadResult.ParentHash = TestItem.KeccakF;
        if (blockHash == getPayloadResult.BlockHash && TryCalculateHash(getPayloadResult, out Hash256? hash))
        {
            getPayloadResult.BlockHash = hash;
        }

        ResultWrapper<PayloadStatusV1> executePayloadResult = await rpc.engine_newPayloadV1(getPayloadResult);
        Assert.That(executePayloadResult.Data.Status, Is.EqualTo(PayloadStatus.Syncing));
    }

    [TestCaseSource(nameof(WrongInputTestsV1))]
    public async Task executePayloadV1_rejects_incorrect_input(Action<ExecutionPayload> breakerAction)
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        ExecutionPayload getPayloadResult = await BuildAndGetPayloadResult(chain, rpc);
        breakerAction(getPayloadResult);
        if (TryCalculateHash(getPayloadResult, out Hash256? hash))
        {
            getPayloadResult.BlockHash = hash;
        }

        ResultWrapper<PayloadStatusV1> executePayloadResult = await rpc.engine_newPayloadV1(getPayloadResult);
        Assert.That(executePayloadResult.Data.Status, Is.EqualTo(PayloadStatus.Invalid));
    }

    [Test]
    public async Task executePayloadV1_rejects_invalid_blockHash()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        ExecutionPayload getPayloadResult = await BuildAndGetPayloadResult(chain, rpc);
        getPayloadResult.BlockHash = TestItem.KeccakC;

        ResultWrapper<PayloadStatusV1> executePayloadResult = await rpc.engine_newPayloadV1(getPayloadResult);
        Assert.That(executePayloadResult.Data.Status, Is.EqualTo(PayloadStatus.Invalid));
    }

    [Test]
    public async Task executePayloadV1_invalid_hash_returns_invalid_and_does_not_store_bad_block()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        ExecutionPayload payload = await BuildAndGetPayloadResult(chain, rpc);
        payload.BlockHash = TestItem.KeccakC;

        ResultWrapper<PayloadStatusV1> result = await rpc.engine_newPayloadV1(payload);

        Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Invalid));
        // Hash-mismatched payloads are not stored in debug_getBadBlocks: block.Hash is the
        // caller-supplied (unverified) value, and ReportBadBlock would write it to _invalidBlocks,
        // which would prevent the legitimate block from being inserted if the CL later sends
        IBadBlockStore badBlockStore = chain.Container.Resolve<IBadBlockStore>();
        Assert.That(badBlockStore.GetAll(), Is.Empty);
    }

    [Test]
    public async Task executePayloadV1_invalid_hash_does_not_poison_chain_tracker()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 genesisHash = chain.BlockTree.HeadHash;
        await ProduceBranchV1(rpc, chain, 1, CreateParentBlockRequestOnHead(chain.BlockTree), setHead: true);

        // Build block 2 on block 1 (BuildAndGetPayloadResult sees block1 as head),
        // then corrupt BlockHash to genesisHash
        ExecutionPayload block2 = await BuildAndGetPayloadResult(chain, rpc);
        Hash256 realBlock2Hash = block2.BlockHash!;
        block2.BlockHash = genesisHash;

        ResultWrapper<PayloadStatusV1> invalidResult = await rpc.engine_newPayloadV1(block2);
        Assert.That(invalidResult.Data.Status, Is.EqualTo(PayloadStatus.Invalid));

        block2.BlockHash = realBlock2Hash;
        ResultWrapper<PayloadStatusV1> validResult = await rpc.engine_newPayloadV1(block2);
        Assert.That(validResult.Data.Status, Is.EqualTo(PayloadStatus.Valid));
    }

    [Test]
    public async Task executePayloadV1_invalid_orphan_records_bad_block()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        ExecutionPayload payload = await BuildAndGetPayloadResult(chain, rpc);
        ulong blockNumber = payload.BlockNumber;

        // Detach from any known parent so `NewPayloadHandler` enters the orphaned-block validation
        // branch, then break a header-level invariant (`GasUsed > GasLimit`) so
        // `IBlockValidator.ValidateOrphanedBlock` fails and `RecordBadBlock` is invoked.
        payload.ParentHash = TestItem.KeccakF;
        payload.GasUsed = payload.GasLimit + 1;
        if (TryCalculateHash(payload, out Hash256? hash))
        {
            payload.BlockHash = hash;
        }

        ResultWrapper<PayloadStatusV1> result = await rpc.engine_newPayloadV1(payload);

        Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Invalid));
        IBadBlockStore badBlockStore = chain.Container.Resolve<IBadBlockStore>();
        Assert.That(badBlockStore.GetAll().Single().Number, Is.EqualTo(blockNumber));
    }

    [Test]
    public async Task executePayloadV1_invalid_suggested_records_bad_block()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;

        // Build parent + child on top of head, then insert only the parent header (BeaconBlockInsert).
        // Because the parent header is known but never processed, `ShouldProcessBlock` returns `false`
        // for the child, routing it through the `ValidateSuggestedBlock` branch. Break a parent-relative
        // header invariant (`Timestamp <= parent.Timestamp`) so suggested-block validation fails and
        // `RecordBadBlock` is invoked.
        ExecutionPayload headPayload = ExecutionPayload.Create(chain.BlockTree.Head!);
        ExecutionPayload[] branch = CreateBlockRequestBranch(chain, headPayload, Address.Zero, 2);
        ExecutionPayload parentPayload = branch[0];
        ExecutionPayload childPayload = branch[1];

        chain.BlockTree.Insert(
            parentPayload.TryGetBlock().Data!.Header,
            BlockTreeInsertHeaderOptions.BeaconBlockInsert | BlockTreeInsertHeaderOptions.MoveToBeaconMainChain);

        childPayload.Timestamp = parentPayload.Timestamp;
        if (TryCalculateHash(childPayload, out Hash256? childHash))
        {
            childPayload.BlockHash = childHash;
        }
        ulong childNumber = childPayload.BlockNumber;

        ResultWrapper<PayloadStatusV1> result = await rpc.engine_newPayloadV1(childPayload);

        Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Invalid));
        IBadBlockStore badBlockStore = chain.Container.Resolve<IBadBlockStore>();
        Assert.That(badBlockStore.GetAll().Single().Number, Is.EqualTo(childNumber));
    }

    [Test]
    public async Task executePayloadV1_rejects_block_with_invalid_timestamp()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        ExecutionPayload getPayloadResult = await BuildAndGetPayloadResult(chain, rpc);
        getPayloadResult.Timestamp = chain.BlockTree.Head!.Timestamp - 1;
        Block? block = getPayloadResult.TryGetBlock().Data;
        getPayloadResult.BlockHash = block!.Header.CalculateHash();

        ResultWrapper<PayloadStatusV1> executePayloadResult = await rpc.engine_newPayloadV1(getPayloadResult);
        Assert.That(executePayloadResult.Data.Status, Is.EqualTo(PayloadStatus.Invalid));
    }

    [Test]
    public async Task executePayloadV1_rejects_block_with_invalid_receiptsRoot()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        ExecutionPayload getPayloadResult = await BuildAndGetPayloadResult(chain, rpc);
        getPayloadResult.ReceiptsRoot = TestItem.KeccakA;
        Block? block = getPayloadResult.TryGetBlock().Data;
        getPayloadResult.BlockHash = block!.Header.CalculateHash();

        ResultWrapper<PayloadStatusV1> executePayloadResult = await rpc.engine_newPayloadV1(getPayloadResult);
        Assert.That(executePayloadResult.Data.Status, Is.EqualTo(PayloadStatus.Invalid));
        Assert.That(chain.BlockFinder.SearchForBlock(new BlockParameter(getPayloadResult.BlockHash)).IsError, Is.True);
    }

    [Test]
    public async Task executePayloadV1_result_is_fail_when_blockchain_processor_reports_exception()
    {
        using MergeTestBlockchain chain = await CreateBaseBlockchain()
            .Build(new TestSingleReleaseSpecProvider(London.Instance));
        IEngineRpcModule rpc = chain.EngineRpcModule;

        ((TestBranchProcessorInterceptor)chain.BranchProcessor).ExceptionToThrow =
            new Exception("unexpected exception");

        ExecutionPayload executionPayload = CreateBlockRequest(chain, CreateParentBlockRequestOnHead(chain.BlockTree), TestItem.AddressD);
        ResultWrapper<PayloadStatusV1> resultWrapper = await rpc.engine_newPayloadV1(executionPayload);
        Assert.That(resultWrapper.Result.ResultType, Is.EqualTo(ResultType.Failure));
    }


    [TestCase(true)]
    [TestCase(false)]
    [CancelAfter(60000)]
    public virtual async Task executePayloadV1_accepts_already_known_block(bool throttleBlockProcessor, CancellationToken cancellationToken)
    {
        using MergeTestBlockchain chain = await CreateBaseBlockchain()
            .ThrottleBlockProcessor(throttleBlockProcessor ? 100 : 0)
            .Build(new TestSingleReleaseSpecProvider(London.Instance));

        IEngineRpcModule rpc = chain.EngineRpcModule;
        Block block = Build.A.Block.WithNumber(1).WithParent(chain.BlockTree.Head!).WithDifficulty(0).WithNonce(0)
            .WithStateRoot(new Hash256("0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f"))
            .TestObject;
        block.Header.IsPostMerge = true;
        block.Header.Hash = block.CalculateHash();
        using SemaphoreSlim bestBlockProcessed = new(0);
        chain.BlockTree.NewHeadBlock += (s, e) =>
        {
            if (e.Block.Hash == block!.Hash)
                bestBlockProcessed.Release(1);
        };
        await chain.BlockTree.SuggestBlockAsync(block!);

        await bestBlockProcessed.WaitAsync(cancellationToken);
        ExecutionPayload blockRequest = ExecutionPayload.Create(block);
        ResultWrapper<PayloadStatusV1> executePayloadResult = await rpc.engine_newPayloadV1(blockRequest);
        Assert.That(executePayloadResult.Data.Status, Is.EqualTo(PayloadStatus.Valid));
    }

    [Test]
    public async Task forkchoiceUpdatedV1_should_work_with_zero_keccak_for_finalization()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;
        ExecutionPayload executionPayload = await SendNewBlockV1(rpc, chain);

        Hash256 newHeadHash = executionPayload.BlockHash;
        ForkchoiceStateV1 forkchoiceStateV1 = new(newHeadHash!, Keccak.Zero, startingHead);
        ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult = await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(forkchoiceUpdatedResult.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(forkchoiceUpdatedResult.Data.PayloadId, Is.EqualTo(null));

            Hash256 actualHead = chain.BlockTree.HeadHash;
            Assert.That(actualHead, Is.Not.EqualTo(startingHead));
            Assert.That(actualHead, Is.EqualTo(newHeadHash));
        }
        AssertExecutionStatusChanged(chain.BlockFinder, newHeadHash!, Keccak.Zero, startingHead);
    }

    [Test]
    public async Task forkchoiceUpdatedV1_should_update_finalized_block_hash()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        TestRpcBlockchain testRpc = await CreateTestRpc(chain);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;
        ExecutionPayload executionPayload = await SendNewBlockV1(rpc, chain);

        Hash256 newHeadHash = executionPayload.BlockHash;
        ForkchoiceStateV1 forkchoiceStateV1 = new(newHeadHash!, startingHead, startingHead!);
        ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult = await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1);

        Hash256? actualFinalizedHash = chain.BlockTree.FinalizedHash;
        BlockForRpc blockForRpc = testRpc.EthRpcModule.eth_getBlockByNumber(BlockParameter.Finalized).Data;
        Assert.That(blockForRpc, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(forkchoiceUpdatedResult.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(forkchoiceUpdatedResult.Data.PayloadId, Is.EqualTo(null));

            Assert.That(actualFinalizedHash, Is.Not.Null);
            Assert.That(actualFinalizedHash, Is.EqualTo(startingHead));

            Assert.That(blockForRpc.Hash, Is.Not.Null);
            Assert.That(blockForRpc.Hash, Is.EqualTo(startingHead));

            Assert.That(chain.BlockTree.FinalizedHash, Is.EqualTo(blockForRpc.Hash));
        }
        AssertExecutionStatusChanged(chain.BlockFinder, newHeadHash!, startingHead, startingHead);
    }

    [Test]
    public async Task forkchoiceUpdatedV1_should_update_safe_block_hash()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        TestRpcBlockchain testRpc = await CreateTestRpc(chain);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;
        ExecutionPayload executionPayload = await SendNewBlockV1(rpc, chain);

        Hash256 newHeadHash = executionPayload.BlockHash;
        ForkchoiceStateV1 forkchoiceStateV1 = new(newHeadHash!, startingHead, startingHead!);
        ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult = await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1);

        Hash256? actualSafeHash = chain.BlockTree.SafeHash;
        BlockForRpc blockForRpc = testRpc.EthRpcModule.eth_getBlockByNumber(BlockParameter.Safe).Data;
        Assert.That(blockForRpc, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(forkchoiceUpdatedResult.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(forkchoiceUpdatedResult.Data.PayloadId, Is.EqualTo(null));

            Assert.That(actualSafeHash, Is.Not.Null);
            Assert.That(actualSafeHash, Is.EqualTo(startingHead));

            Assert.That(blockForRpc.Hash, Is.Not.Null);
            Assert.That(blockForRpc.Hash, Is.EqualTo(startingHead));
        }

        AssertExecutionStatusChanged(chain.BlockFinder, newHeadHash!, startingHead, startingHead);
    }


    [Test]
    public async Task forkchoiceUpdatedV1_should_work_with_zero_keccak_as_safe_block()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;
        ExecutionPayload executionPayload = await SendNewBlockV1(rpc, chain);

        Hash256 newHeadHash = executionPayload.BlockHash!;
        ForkchoiceStateV1 forkchoiceStateV1 = new(newHeadHash, newHeadHash, Keccak.Zero);
        ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult = await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(forkchoiceUpdatedResult.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(forkchoiceUpdatedResult.Data.PayloadId, Is.EqualTo(null));

            Hash256 actualHead = chain.BlockTree.HeadHash;
            Assert.That(actualHead, Is.Not.EqualTo(startingHead));
            Assert.That(actualHead, Is.EqualTo(newHeadHash));
        }
        AssertExecutionStatusChanged(chain.BlockFinder, newHeadHash!, newHeadHash, Keccak.Zero);
    }

    [Test]
    public async Task forkchoiceUpdatedV1_with_no_payload_attributes_should_change_head()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;
        ExecutionPayload executionPayload = await SendNewBlockV1(rpc, chain);

        Hash256 newHeadHash = executionPayload.BlockHash!;
        ForkchoiceStateV1 forkchoiceStateV1 = new(newHeadHash, startingHead, startingHead);
        ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult = await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(forkchoiceUpdatedResult.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(forkchoiceUpdatedResult.Data.PayloadId, Is.EqualTo(null));

            Hash256 actualHead = chain.BlockTree.HeadHash;
            Assert.That(actualHead, Is.Not.EqualTo(startingHead));
            Assert.That(actualHead, Is.EqualTo(newHeadHash));
        }
        AssertExecutionStatusChanged(chain.BlockFinder, newHeadHash, startingHead, startingHead);
    }

    [Test]
    public async Task forkChoiceUpdatedV1_to_unknown_block_fails()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        ForkchoiceStateV1 forkchoiceStateV1 = new(TestItem.KeccakF, TestItem.KeccakF, TestItem.KeccakF);
        ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult = await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1);
        Assert.That(forkchoiceUpdatedResult.Data.PayloadStatus.Status, Is.EqualTo(nameof(PayloadStatus.Syncing).ToUpper())); // ToDo wait for final PostMerge sync
        AssertExecutionStatusNotChanged(chain.BlockFinder, TestItem.KeccakF, TestItem.KeccakF, TestItem.KeccakF);
    }

    [Test]
    public async Task forkChoiceUpdatedV1_to_unknown_safeBlock_hash_should_fail()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;
        ExecutionPayload executionPayload = await SendNewBlockV1(rpc, chain);

        Hash256 newHeadHash = executionPayload.BlockHash!;
        ForkchoiceStateV1 forkchoiceStateV1 = new(newHeadHash, startingHead, TestItem.KeccakF);
        ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult = await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1, null);
        Assert.That(forkchoiceUpdatedResult.ErrorCode, Is.EqualTo(MergeErrorCodes.InvalidForkchoiceState));

        Hash256 actualHead = chain.BlockTree.HeadHash;
        Assert.That(actualHead, Is.Not.EqualTo(newHeadHash));
    }

    [Test]
    public async Task forkChoiceUpdatedV1_no_common_branch_fails()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256? startingHead = chain.BlockTree.HeadHash;
        Block parent = Build.A.Block.WithNumber(2).WithParentHash(TestItem.KeccakA).WithNonce(0).WithDifficulty(0).TestObject;
        Block block = Build.A.Block.WithNumber(3).WithParent(parent).WithNonce(0).WithDifficulty(0).TestObject;

        await rpc.engine_newPayloadV1(ExecutionPayload.Create(parent));

        ForkchoiceStateV1 forkchoiceStateV1 = new(parent.Hash!, startingHead, startingHead);
        ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult = await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1);
        Assert.That(forkchoiceUpdatedResult.Data.PayloadStatus.Status, Is.EqualTo("SYNCING"));

        await rpc.engine_newPayloadV1(ExecutionPayload.Create(block));

        ForkchoiceStateV1 forkchoiceStateV11 = new(parent.Hash!, startingHead, startingHead);
        ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult_1 = await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV11);
        Assert.That(forkchoiceUpdatedResult_1.Data.PayloadStatus.Status, Is.EqualTo("SYNCING"));

        AssertExecutionStatusNotChanged(chain.BlockFinder, block.Hash!, startingHead, startingHead);
    }

    [Test, NonParallelizable]
    public async Task forkChoiceUpdatedV1_block_still_processing()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(mergeConfig: new MergeConfig()
        {
            NewPayloadBlockProcessingTimeout = 100
        });

        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;
        Block blockTreeHead = chain.BlockTree.Head!;
        Block block = Build.A.Block.WithNumber(blockTreeHead.Number + 1).WithParent(blockTreeHead).WithNonce(0).WithDifficulty(0).TestObject;

        chain.ThrottleBlockProcessor(1000);
        ManualResetEventSlim processingStarted = new(false);
        ((TestBranchProcessorInterceptor)chain.BranchProcessor).ProcessingStarted = processingStarted;

        // Directly enqueue a block to occupy the processor (bypasses the RPC semaphore),
        // ensuring subsequent blocks route through the recovery queue (slow path)
        Block occupyBlock = Build.A.Block.WithNumber(blockTreeHead.Number + 1).WithParent(blockTreeHead)
            .WithNonce(0).WithDifficulty(0).WithStateRoot(blockTreeHead.StateRoot!).TestObject;
        occupyBlock.Header.TotalDifficulty = blockTreeHead.TotalDifficulty;
        _ = Task.Run(async () => await chain.BlockProcessingQueue.Enqueue(
            occupyBlock, ProcessingOptions.ForceProcessing | ProcessingOptions.DoNotUpdateHead));
        processingStarted.Wait(TimeSpan.FromSeconds(5));

        ResultWrapper<PayloadStatusV1> newPayloadV1 =
            await rpc.engine_newPayloadV1(ExecutionPayload.Create(block));
        Assert.That(newPayloadV1.Data.Status, Is.EqualTo("SYNCING"));

        ForkchoiceStateV1 forkchoiceStateV1 = new(block.Hash!, startingHead, startingHead);
        ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult =
            await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1);
        Assert.That(forkchoiceUpdatedResult.Data.PayloadStatus.Status, Is.EqualTo("SYNCING"));

        AssertExecutionStatusNotChanged(chain.BlockFinder, block.Hash!, startingHead, startingHead);
    }

    [Test, NonParallelizable]
    public async Task AlreadyKnown_not_cached_block_should_return_valid()
    {
        // Disable the latestBlocks cache so the second b5 submission below routes
        // through the AddBlockResult.AlreadyKnown branch (what the test name asserts)
        // rather than a cache hit.
        using MergeTestBlockchain? chain = await CreateBlockchain(mergeConfig: new MergeConfig()
        {
            NewPayloadCacheSize = 0
        });

        IEngineRpcModule? rpc = chain.EngineRpcModule;
        Block? head = chain.BlockTree.Head!;

        Block? b4 = Build.A.Block
            .WithNumber(head.Number + 1)
            .WithParent(head)
            .WithNonce(0)
            .WithDifficulty(0)
            .WithStateRoot(head.StateRoot!)
            .WithBeneficiary(Build.An.Address.TestObject)
            .TestObject;

        Assert.That((await rpc.engine_newPayloadV1(ExecutionPayload.Create(b4))).Data.Status, Is.EqualTo(PayloadStatus.Valid));

        Block? b5 = Build.A.Block
            .WithNumber(b4.Number + 1)
            .WithParent(b4)
            .WithNonce(0)
            .WithDifficulty(0)
            .WithStateRoot(b4.StateRoot!)
            .TestObject;

        Assert.That((await rpc.engine_newPayloadV1(ExecutionPayload.Create(b5))).Data.Status, Is.EqualTo(PayloadStatus.Valid));
        Assert.That((await rpc.engine_newPayloadV1(ExecutionPayload.Create(b5))).Data.Status, Is.EqualTo(PayloadStatus.Valid));
    }

    [Test, NonParallelizable]
    public async Task Invalid_block_on_processing_wont_be_accepted_if_sent_twice_in_a_row_when_block_processing_queue_is_not_empty()
    {
        using MergeTestBlockchain? chain = await CreateBlockchain(mergeConfig: new MergeConfig()
        {
            NewPayloadBlockProcessingTimeout = 100
        });

        IEngineRpcModule? rpc = chain.EngineRpcModule;
        Block? head = chain.BlockTree.Head!;

        // make sure AddressA has enough balance to send tx
        Assert.That(chain.ReadOnlyState.GetBalance(TestItem.AddressA), Is.GreaterThan(UInt256.One));

        // block is an invalid block, but it is impossible to detect until we process it.
        // it is invalid because after you process its transactions, the root of the state trie
        // doesn't match the state root in the block
        Block? block = Build.A.Block
            .WithNumber(head.Number + 1)
            .WithParent(head)
            .WithNonce(0)
            .WithDifficulty(0)
            .WithTransactions(
                Build.A.Transaction
                    .WithTo(TestItem.AddressD)
                    .WithValue(100.GWei)
                    .SignedAndResolved(TestItem.PrivateKeyA)
                    .TestObject
            )
            .WithGasUsed(21000)
            .WithStateRoot(head.StateRoot!) // after processing transaction, this state root is wrong
            .TestObject;

        chain.ThrottleBlockProcessor(1000); // throttle the block processor enough so that the block processing queue is never empty
        ManualResetEventSlim processingStarted = new(false);
        ((TestBranchProcessorInterceptor)chain.BranchProcessor).ProcessingStarted = processingStarted;

        // Directly enqueue a block to occupy the processor (bypasses the RPC semaphore),
        // ensuring subsequent blocks route through the recovery queue (slow path)
        Block occupyBlock = Build.A.Block.WithNumber(head.Number + 1).WithParent(head)
            .WithNonce(0).WithDifficulty(0).WithStateRoot(head.StateRoot!).TestObject;
        occupyBlock.Header.TotalDifficulty = head.TotalDifficulty;
        _ = Task.Run(async () => await chain.BlockProcessingQueue.Enqueue(
            occupyBlock, ProcessingOptions.ForceProcessing | ProcessingOptions.DoNotUpdateHead));
        processingStarted.Wait(TimeSpan.FromSeconds(5));

        Assert.That((await rpc.engine_newPayloadV1(ExecutionPayload.Create(block))).Data.Status, Is.EqualTo(PayloadStatus.Syncing));
        Assert.That((await rpc.engine_newPayloadV1(ExecutionPayload.Create(block))).Data.Status, Is.AnyOf(PayloadStatus.Syncing));
    }

    [Test]
    public async Task forkchoiceUpdatedV1_should_change_head_when_all_parameters_are_the_newHeadHash()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        ExecutionPayload executionPayload = await SendNewBlockV1(rpc, chain);
        Hash256 newHeadHash = executionPayload.BlockHash;
        ForkchoiceStateV1 forkchoiceStateV1 = new(newHeadHash, newHeadHash, newHeadHash);
        ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult =
            await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1, null);
        Assert.That(forkchoiceUpdatedResult.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
        Assert.That(forkchoiceUpdatedResult.Data.PayloadId, Is.EqualTo(null));
        AssertExecutionStatusChanged(chain.BlockFinder, newHeadHash, newHeadHash, newHeadHash);
    }

    [Test]
    public async Task Can_transition_from_PoW_chain()
    {
        using MergeTestBlockchain chain =
            await CreateBlockchain(null, new MergeConfig() { TerminalTotalDifficulty = "1000001" });
        IEngineRpcModule rpc = chain.EngineRpcModule;

        // adding PoW block
        await chain.AddBlockThroughPoW();

        // creating PoS block
        Block? head = chain.BlockTree.Head;
        ExecutionPayload executionPayload = await SendNewBlockV1(rpc, chain);
        await rpc.engine_forkchoiceUpdatedV1(
            new ForkchoiceStateV1(executionPayload.BlockHash, executionPayload.BlockHash, executionPayload.BlockHash));
        Assert.That(chain.BlockTree.Head!.Number, Is.EqualTo(2));
    }

    [TestCase(null)]
    [TestCase(1000000000)]
    [TestCase(1000001)]
    public async Task executePayloadV1_should_not_accept_blocks_with_incorrect_ttd(long? terminalTotalDifficulty)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(null, new MergeConfig()
        {
            TerminalTotalDifficulty = $"{terminalTotalDifficulty}"
        });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        ExecutionPayload executionPayload = CreateBlockRequest(chain, CreateParentBlockRequestOnHead(chain.BlockTree), TestItem.AddressD);
        ResultWrapper<PayloadStatusV1> resultWrapper = await rpc.engine_newPayloadV1(executionPayload);
        Assert.That(resultWrapper.Data.Status, Is.EqualTo(PayloadStatus.Invalid));
        Assert.That(resultWrapper.Data.LatestValidHash, Is.EqualTo(Keccak.Zero));
    }

    [TestCase(null)]
    [TestCase(1000000000)]
    [TestCase(1000001)]
    public async Task forkchoiceUpdatedV1_should_not_accept_blocks_with_incorrect_ttd(long? terminalTotalDifficulty)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(null, new MergeConfig()
        {
            TerminalTotalDifficulty = $"{terminalTotalDifficulty}"
        });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 blockHash = chain.BlockTree.HeadHash;
        ResultWrapper<ForkchoiceUpdatedV1Result> resultWrapper = await rpc.engine_forkchoiceUpdatedV1(new ForkchoiceStateV1(blockHash, blockHash, blockHash), null);
        Assert.That(resultWrapper.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Invalid));
        Assert.That(resultWrapper.Data.PayloadStatus.LatestValidHash, Is.EqualTo(Keccak.Zero));
    }

    [CancelAfter(30000)]
    public async Task executePayloadV1_on_top_of_terminal_block(CancellationToken cancellationToken)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(null, new MergeConfig()
        {
            TerminalTotalDifficulty = $"{1900000}"
        });
        IEngineRpcModule rpc = chain.EngineRpcModule;

        Block newBlock = BuildNewBlock(chain.BlockTree.Head!).TestObject;
        newBlock.CalculateHash();

        Block oneMoreTerminalBlock = BuildOneMoreTerminalBlock(chain.BlockTree.Head!).TestObject;
        oneMoreTerminalBlock.CalculateHash();

        using SemaphoreSlim bestBlockProcessed = new(0);
        chain.BlockTree.NewHeadBlock += (s, e) =>
        {
            if (e.Block.Hash == newBlock!.Hash)
                bestBlockProcessed.Release(1);
        };
        await chain.BlockTree.SuggestBlockAsync(newBlock);
        await bestBlockProcessed.WaitAsync(cancellationToken);

        await chain.BlockTree.SuggestBlockAsync(oneMoreTerminalBlock);

        Block firstPoSBlock = Build.A.Block.WithParent(oneMoreTerminalBlock).
            WithNumber(oneMoreTerminalBlock.Number + 1)
            .WithStateRoot(new Hash256("0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f"))
            .WithDifficulty(0).WithNonce(0).TestObject;
        firstPoSBlock.CalculateHash();
        ExecutionPayload executionPayload = ExecutionPayload.Create(firstPoSBlock);
        ResultWrapper<PayloadStatusV1> resultWrapper = await rpc.engine_newPayloadV1(executionPayload);
        Assert.That(resultWrapper.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        Assert.That(JToken.Parse(chain.JsonSerializer.Serialize(ExecutionPayload.Create(chain.BlockTree.BestSuggestedBody!))), Is.EqualTo(JToken.Parse(chain.JsonSerializer.Serialize(executionPayload))).Using(JToken.EqualityComparer));
    }

    [CancelAfter(30000)]
    public async Task executePayloadV1_on_top_of_not_processed_invalid_terminal_block(CancellationToken cancellationToken)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(null, new MergeConfig()
        {
            TerminalTotalDifficulty = $"{1900000}"
        });
        IEngineRpcModule rpc = chain.EngineRpcModule;

        Block newBlock = BuildNewBlock(chain.BlockTree.Head!).TestObject;
        newBlock.CalculateHash();

        Block oneMoreTerminalBlock = BuildOneMoreTerminalBlock(chain.BlockTree.Head!, correctStateRoot: false).TestObject;
        oneMoreTerminalBlock.CalculateHash();

        using SemaphoreSlim bestBlockProcessed = new(0);
        chain.BlockTree.NewHeadBlock += (s, e) =>
        {
            if (e.Block.Hash == newBlock!.Hash)
                bestBlockProcessed.Release(1);
        };
        await chain.BlockTree.SuggestBlockAsync(newBlock);
        await bestBlockProcessed.WaitAsync(cancellationToken);

        await chain.BlockTree.SuggestBlockAsync(oneMoreTerminalBlock);

        Block firstPoSBlock = Build.A.Block.WithParent(oneMoreTerminalBlock).
            WithNumber(oneMoreTerminalBlock.Number + 1)
            .WithStateRoot(new Hash256("0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f"))
            .WithDifficulty(0).WithNonce(0).TestObject;
        firstPoSBlock.CalculateHash();
        ExecutionPayload executionPayload = ExecutionPayload.Create(firstPoSBlock);
        ResultWrapper<PayloadStatusV1> resultWrapper = await rpc.engine_newPayloadV1(executionPayload);
        Assert.That(resultWrapper.Data.Status, Is.EqualTo(PayloadStatus.Invalid));
        Assert.That(resultWrapper.Data.LatestValidHash, Is.EqualTo(Keccak.Zero));
    }

    [Test]
    public async Task executePayloadV1_accepts_first_block()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        ExecutionPayload executionPayload = CreateBlockRequest(chain, CreateParentBlockRequestOnHead(chain.BlockTree), TestItem.AddressD);
        ResultWrapper<PayloadStatusV1> resultWrapper = await rpc.engine_newPayloadV1(executionPayload);
        Assert.That(resultWrapper.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        Assert.That(JToken.Parse(chain.JsonSerializer.Serialize(ExecutionPayload.Create(chain.BlockTree.BestSuggestedBody!))), Is.EqualTo(JToken.Parse(chain.JsonSerializer.Serialize(executionPayload))).Using(JToken.EqualityComparer));
    }

    [Test]
    public async Task executePayloadV1_start_sync_if_parent_has_no_state()
    {
        IStateReader mockedStateReader = Substitute.For<IStateReader>();

        using MergeTestBlockchain chain = await CreateBlockchain(configurer: builder => builder
            .UpdateSingleton<IAsyncHandler<ExecutionPayload, PayloadStatusV1>>(innerBuilder => innerBuilder
                .AddSingleton<IStateReader>(mockedStateReader)));

        IEngineRpcModule rpc = chain.EngineRpcModule;
        ExecutionPayload parent = CreateParentBlockRequestOnHead(chain.BlockTree);
        mockedStateReader.HasStateForBlock(Arg.Any<BlockHeader?>()).Returns(false);

        ExecutionPayload executionPayload = CreateBlockRequest(chain, parent, TestItem.AddressD);
        ResultWrapper<PayloadStatusV1> resultWrapper = await rpc.engine_newPayloadV1(executionPayload);
        Assert.That(resultWrapper.Data.Status, Is.EqualTo(PayloadStatus.Syncing));
    }

    [Test]
    public async Task executePayloadV1_calculate_hash_for_cached_blocks()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        ExecutionPayload executionPayload = CreateBlockRequest(
            chain, CreateParentBlockRequestOnHead(chain.BlockTree),
            TestItem.AddressD);
        ResultWrapper<PayloadStatusV1> resultWrapper = await rpc.engine_newPayloadV1(executionPayload);
        Assert.That(resultWrapper.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        ResultWrapper<PayloadStatusV1>
            resultWrapper2 = await rpc.engine_newPayloadV1(executionPayload);
        Assert.That(resultWrapper2.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        executionPayload.ParentHash = executionPayload.BlockHash!;
        ResultWrapper<PayloadStatusV1> invalidBlockRequest = await rpc.engine_newPayloadV1(executionPayload);
        Assert.That(invalidBlockRequest.Data.Status, Is.EqualTo(PayloadStatus.Invalid));
    }

    [TestCase(30)]
    public async Task can_progress_chain_one_by_one_v1(int count)
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 lastHash = (await ProduceBranchV1(rpc, chain, count, CreateParentBlockRequestOnHead(chain.BlockTree), true))
            .LastOrDefault()?.BlockHash ?? Keccak.Zero;
        Assert.That(chain.BlockTree.HeadHash, Is.EqualTo(lastHash));
        Block? last = RunForAllBlocksInBranch(chain.BlockTree, chain.BlockTree.HeadHash, static b => b.IsGenesis, true);
        Assert.That(last, Is.Not.Null);
        Assert.That(last!.IsGenesis, Is.True);
    }

    [Test]
    public async Task forkchoiceUpdatedV1_can_reorganize_to_last_block()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;

        async Task CanReorganizeToBlock(ExecutionPayload block, MergeTestBlockchain testChain)
        {
            ForkchoiceStateV1 forkchoiceStateV1 = new(block.BlockHash, block.BlockHash, block.BlockHash);
            ResultWrapper<ForkchoiceUpdatedV1Result> result = await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1, null);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
                Assert.That(result.Data.PayloadId, Is.EqualTo(null));
                Assert.That(testChain.BlockTree.HeadHash, Is.EqualTo(block.BlockHash));
                Assert.That(testChain.BlockTree.Head!.Number, Is.EqualTo(block.BlockNumber));
            }
        }

        async Task CanReorganizeToLastBlock(MergeTestBlockchain testChain,
            params IReadOnlyList<ExecutionPayload>[] branches)
        {
            foreach (IReadOnlyList<ExecutionPayload>? branch in branches)
            {
                await CanReorganizeToBlock(branch.Last(), testChain);
            }
        }

        IReadOnlyList<ExecutionPayload> branch1 = await ProduceBranchV1(rpc, chain, 10, CreateParentBlockRequestOnHead(chain.BlockTree), true);
        // setHead: false - sibling production here only builds alternative payloads; the reorg
        // assertions below exercise forkchoice updates explicitly.
        IReadOnlyList<ExecutionPayload> branch2 = await ProduceBranchV1(rpc, chain, 6, branch1[3], setHead: false, TestItem.KeccakC);

        await CanReorganizeToLastBlock(chain, branch1, branch2);
    }

    [Test]
    public async Task forkchoiceUpdatedV1_head_block_after_reorg()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;

        async Task CanReorganizeToBlock(ExecutionPayload block, MergeTestBlockchain testChain)
        {
            ForkchoiceStateV1 forkchoiceStateV1 = new(block.BlockHash, block.BlockHash, block.BlockHash);
            ResultWrapper<ForkchoiceUpdatedV1Result> result = await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1, null);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
                Assert.That(result.Data.PayloadId, Is.EqualTo(null));
                Assert.That(testChain.BlockTree.HeadHash, Is.EqualTo(block.BlockHash));
                Assert.That(testChain.BlockTree.Head!.Number, Is.EqualTo(block.BlockNumber));
            }
        }

        IReadOnlyList<ExecutionPayload> branch1 = await ProduceBranchV1(rpc, chain, 10, CreateParentBlockRequestOnHead(chain.BlockTree), true);
        // setHead=false on the sibling branch — see comment in forkchoiceUpdatedV1_can_reorganize_to_last_block.
        IReadOnlyList<ExecutionPayload> branch2 = await ProduceBranchV1(rpc, chain, 6, branch1[3], setHead: false, TestItem.KeccakC);

        await CanReorganizeToBlock(branch2.Last(), chain);
    }

    [Test]
    public async Task newPayloadV1_should_return_accepted_for_side_branch()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        ExecutionPayload executionPayload = CreateBlockRequest(chain, CreateParentBlockRequestOnHead(chain.BlockTree), TestItem.AddressD);
        ResultWrapper<PayloadStatusV1> resultWrapper = await rpc.engine_newPayloadV1(executionPayload);
        Assert.That(resultWrapper.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        ForkchoiceStateV1 forkChoiceUpdatedRequest = new(executionPayload.BlockHash, executionPayload.BlockHash, executionPayload.BlockHash);
        ResultWrapper<ForkchoiceUpdatedV1Result> fcu1 = (await rpc.engine_forkchoiceUpdatedV1(forkChoiceUpdatedRequest,
            new PayloadAttributes()
            {
                PrevRandao = TestItem.KeccakA,
                SuggestedFeeRecipient = Address.Zero,
                Timestamp = executionPayload.Timestamp + 1
            }));
        await rpc.engine_getPayloadV1(Bytes.FromHexString(fcu1.Data.PayloadId!));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task executePayloadV1_processes_passed_transactions(bool moveHead)
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        IReadOnlyList<ExecutionPayload> branch = await ProduceBranchV1(rpc, chain, 8, CreateParentBlockRequestOnHead(chain.BlockTree), moveHead);

        foreach (ExecutionPayload block in branch)
        {
            uint count = 10;
            ExecutionPayload executePayloadRequest = CreateBlockRequest(chain, block, TestItem.AddressA);
            PrivateKey from = TestItem.PrivateKeyB;
            Address to = TestItem.AddressD;
            (_, UInt256 toBalanceAfter) = AddTransactions(chain, executePayloadRequest, from, to, count, 1, out BlockHeader? parentHeader);

            executePayloadRequest.GasUsed = GasCostOf.Transaction * count;
            executePayloadRequest.StateRoot = new Hash256("0x3d2e3ced6da0d1e94e65894dc091190480f045647610ef614e1cab4241ca66e0");
            executePayloadRequest.ReceiptsRoot = new Hash256("0xb34a29e4a30ab5d32fdbc0292a97ac1cf1028c085f538dec2d91d91c6d0b0562");
            TryCalculateHash(executePayloadRequest, out Hash256? hash);
            executePayloadRequest.BlockHash = hash;
            ResultWrapper<PayloadStatusV1> result = await rpc.engine_newPayloadV1(executePayloadRequest);
            Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid));

            BlockHeader? payloadBlock = chain.BlockFinder.FindHeader(executePayloadRequest.BlockHash);
            Assert.That(chain.StateReader.HasStateForBlock(payloadBlock), Is.True);
            Assert.That(chain.StateReader.GetBalance(payloadBlock, to), Is.EqualTo(toBalanceAfter));
            if (moveHead)
            {
                ForkchoiceStateV1 forkChoiceUpdatedRequest = new(executePayloadRequest.BlockHash, executePayloadRequest.BlockHash, executePayloadRequest.BlockHash);
                await rpc.engine_forkchoiceUpdatedV1(forkChoiceUpdatedRequest);
                Assert.That(chain.ReadOnlyState.StateRoot, Is.EqualTo(executePayloadRequest.StateRoot));
                Assert.That(chain.ReadOnlyState.StateRoot, Is.Not.EqualTo(parentHeader.StateRoot!));
            }
        }
    }

    [Test]
    public async Task executePayloadV1_transactions_produce_receipts()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        IReadOnlyList<ExecutionPayload> branch = await ProduceBranchV1(rpc, chain, 1, CreateParentBlockRequestOnHead(chain.BlockTree), false);

        foreach (ExecutionPayload block in branch)
        {
            uint count = 10;
            ExecutionPayload executionPayload = CreateBlockRequest(chain, block, TestItem.AddressA);
            PrivateKey from = TestItem.PrivateKeyB;
            Address to = TestItem.AddressD;
            (_, UInt256 toBalanceAfter) = AddTransactions(chain, executionPayload, from, to, count, 1, out BlockHeader parentHeader);

            UInt256 fromBalance = chain.StateReader.GetBalance(parentHeader!, from.Address);
            executionPayload.GasUsed = GasCostOf.Transaction * count;
            executionPayload.StateRoot =
                new Hash256("0x3d2e3ced6da0d1e94e65894dc091190480f045647610ef614e1cab4241ca66e0");
            executionPayload.ReceiptsRoot =
                new Hash256("0xb34a29e4a30ab5d32fdbc0292a97ac1cf1028c085f538dec2d91d91c6d0b0562");
            TryCalculateHash(executionPayload, out Hash256 hash);
            executionPayload.BlockHash = hash;
            ResultWrapper<PayloadStatusV1> result = await rpc.engine_newPayloadV1(executionPayload);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid));

                BlockHeader? payloadBlock = chain.BlockFinder.FindHeader(executionPayload.BlockHash);
                Assert.That(chain.StateReader.HasStateForBlock(payloadBlock), Is.True);

                UInt256 fromBalanceAfter = chain.StateReader.GetBalance(payloadBlock, from.Address);
                Assert.That(fromBalanceAfter, Is.LessThan(fromBalance - toBalanceAfter));
                Assert.That(chain.StateReader.GetBalance(payloadBlock, to), Is.EqualTo(toBalanceAfter));
                Block findBlock = chain.BlockTree.FindBlock(executionPayload.BlockHash, BlockTreeLookupOptions.None)!;
                TxReceipt[]? receipts = chain.ReceiptStorage.Get(findBlock);
                Assert.That(findBlock.Transactions.Select(static t => t.Hash), Is.EqualTo(receipts.Select(static r => r.TxHash)));
            }
        }
    }

    [Test]
    public async Task ExecutionPayloadV1_set_and_get_transactions_roundtrip()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        Hash256 startingHead = chain.BlockTree.HeadHash;
        uint count = 3;
        int value = 10;
        Address recipient = TestItem.AddressD;
        PrivateKey sender = TestItem.PrivateKeyB;

        Transaction[] txsSource =
            BuildTransactions(chain, startingHead, sender, recipient, count, value, out _, out _);

        ExecutionPayload executionPayload = new();
        executionPayload.SetTransactions(txsSource);

        Transaction[] txsReceived = executionPayload.TryGetTransactions().Data!;

        Assert.That(txsReceived, Is.EqualTo(txsSource).UsingTransactionComparer(
            nameof(Transaction.ChainId),
            nameof(Transaction.Data),
            nameof(Transaction.SenderAddress),
            nameof(Transaction.Timestamp)));
    }

    [Test]
    public async Task payloadV1_no_suggestedFeeRecipient_in_config()
    {
        using MergeTestBlockchain chain =
            await CreateBlockchain(null, new MergeConfig() { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;
        ulong timestamp = Timestamper.UnixTime.Seconds;
        Hash256 random = Keccak.Zero;
        Address feeRecipient = TestItem.AddressC;
        string payloadId = rpc.engine_forkchoiceUpdatedV1(new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
                new PayloadAttributes { Timestamp = timestamp, SuggestedFeeRecipient = feeRecipient, PrevRandao = random }).Result.Data
            .PayloadId!;
        Assert.That((await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId))).Data!.FeeRecipient, Is.EqualTo(TestItem.AddressC));
    }

    [TestCase(0, "0x0000000000000000000000000000000000000000000000000000000000000000")]
    [TestCase(1000001, "0x191dc9697d77129ee5b6f6d57074d2c854a38129913e3fdd3d9f0ebc930503a6")]
    public async Task exchangeTransitionConfiguration_return_expected_results(long clTtd, string terminalBlockHash)
    {
        using MergeTestBlockchain chain =
            await CreateBlockchain(null, new MergeConfig() { TerminalTotalDifficulty = "1000001", TerminalBlockHash = new Hash256("0x191dc9697d77129ee5b6f6d57074d2c854a38129913e3fdd3d9f0ebc930503a6").ToString(true), TerminalBlockNumber = 1 });
        IEngineRpcModule rpc = chain.EngineRpcModule;

        TransitionConfigurationV1 result = rpc.engine_exchangeTransitionConfigurationV1(new TransitionConfigurationV1()
        {
            TerminalBlockNumber = 0,
            TerminalBlockHash = new Hash256(terminalBlockHash),
            TerminalTotalDifficulty = (UInt256)clTtd
        }).Data;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TerminalTotalDifficulty, Is.EqualTo((UInt256)1000001));
            Assert.That(result.TerminalBlockNumber, Is.EqualTo(1));
            Assert.That(result.TerminalBlockHash.ToString(), Is.EqualTo("0x191dc9697d77129ee5b6f6d57074d2c854a38129913e3fdd3d9f0ebc930503a6"));
        }
    }

    [TestCase(0, "0x0000000000000000000000000000000000000000000000000000000000000000")]
    [TestCase(1000001, "0x191dc9697d77129ee5b6f6d57074d2c854a38129913e3fdd3d9f0ebc930503a6")]
    public async Task exchangeTransitionConfiguration_return_with_empty_Nethermind_configuration(long clTtd, string terminalBlockHash)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(configurer: builder => builder
            .AddDecorator<IMergeConfig>((ctx, mergeConfig) =>
            {
                mergeConfig.TerminalTotalDifficulty = null; // Clear default test config that set a TTD
                return mergeConfig;
            }));
        IEngineRpcModule rpc = chain.EngineRpcModule;

        TransitionConfigurationV1 result = rpc.engine_exchangeTransitionConfigurationV1(new TransitionConfigurationV1()
        {
            TerminalBlockNumber = 0,
            TerminalBlockHash = new Hash256(terminalBlockHash),
            TerminalTotalDifficulty = (UInt256)clTtd
        }).Data;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TerminalTotalDifficulty, Is.EqualTo(UInt256.Parse("115792089237316195423570985008687907853269984665640564039457584007913129638912")));
            Assert.That(result.TerminalBlockNumber, Is.EqualTo(0));
            Assert.That(result.TerminalBlockHash.ToString(), Is.EqualTo("0x0000000000000000000000000000000000000000000000000000000000000000"));
        }
    }

    private async Task<ExecutionPayload> SendNewBlockV1(IEngineRpcModule rpc, MergeTestBlockchain chain)
    {
        ExecutionPayload executionPayload = CreateBlockRequest(
            chain, CreateParentBlockRequestOnHead(chain.BlockTree),
            TestItem.AddressD);
        ResultWrapper<PayloadStatusV1> executePayloadResult =
            await rpc.engine_newPayloadV1(executionPayload);
        Assert.That(executePayloadResult.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        return executionPayload;
    }

    private async Task<ExecutionPayload> BuildAndSendNewBlockV1(IEngineRpcModule rpc, MergeTestBlockchain chain, bool waitForBlockImprovement)
    {
        Hash256 head = chain.BlockTree.HeadHash;
        ulong timestamp = Timestamper.UnixTime.Seconds;
        Hash256 random = Keccak.Zero;
        Address feeRecipient = Address.Zero;
        ExecutionPayload executionPayload = await BuildAndGetPayloadResult(rpc, chain, head,
            Keccak.Zero, head, timestamp, random, feeRecipient, waitForBlockImprovement);
        ResultWrapper<PayloadStatusV1> executePayloadResult =
            await rpc.engine_newPayloadV1(executionPayload);
        Assert.That(executePayloadResult.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        return executionPayload;
    }


    [Test]
    public async Task repeat_the_same_payload_after_fcu_should_return_valid_and_be_ignored()
    {
        using MergeTestBlockchain chain =
            await CreateBlockchain(null, new MergeConfig() { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;

        // Correct new payload
        ExecutionPayload executionPayloadV11 = CreateBlockRequest(
            chain, CreateParentBlockRequestOnHead(chain.BlockTree),
            TestItem.AddressA);
        ResultWrapper<PayloadStatusV1> newPayloadResult1 = await rpc.engine_newPayloadV1(executionPayloadV11);
        Assert.That(newPayloadResult1.Data.Status, Is.EqualTo(PayloadStatus.Valid));

        // Fork choice updated with first np hash
        ForkchoiceStateV1 forkChoiceState1 = new(executionPayloadV11.BlockHash,
            executionPayloadV11.BlockHash,
            executionPayloadV11.BlockHash);
        ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult1 =
            await rpc.engine_forkchoiceUpdatedV1(forkChoiceState1);
        Assert.That(forkchoiceUpdatedResult1.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));

        ResultWrapper<PayloadStatusV1> newPayloadResult2 = await rpc.engine_newPayloadV1(executionPayloadV11);
        Assert.That(newPayloadResult2.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        Assert.That(newPayloadResult2.Data.LatestValidHash, Is.EqualTo(executionPayloadV11.BlockHash));
    }

    [Test]
    public async Task payloadV1_invalid_parent_hash()
    {
        using MergeTestBlockchain chain =
            await CreateBlockchain(null, new MergeConfig() { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;

        // Correct new payload
        ExecutionPayload executionPayloadV11 = CreateBlockRequest(
            chain, CreateParentBlockRequestOnHead(chain.BlockTree),
            TestItem.AddressA);
        ResultWrapper<PayloadStatusV1> newPayloadResult1 = await rpc.engine_newPayloadV1(executionPayloadV11);
        Assert.That(newPayloadResult1.Data.Status, Is.EqualTo(PayloadStatus.Valid));

        // Fork choice updated with first np hash
        ForkchoiceStateV1 forkChoiceState1 = new(executionPayloadV11.BlockHash, executionPayloadV11.BlockHash,
            executionPayloadV11.BlockHash);
        ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult1 = await rpc.engine_forkchoiceUpdatedV1(forkChoiceState1);
        Assert.That(forkchoiceUpdatedResult1.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));

        // New payload unknown parent hash
        ExecutionPayload executionPayloadV12A = CreateBlockRequest(chain, executionPayloadV11, TestItem.AddressA);
        executionPayloadV12A.ParentHash = TestItem.KeccakB;
        TryCalculateHash(executionPayloadV12A, out Hash256? hash);
        executionPayloadV12A.BlockHash = hash;
        ResultWrapper<PayloadStatusV1> newPayloadResult2A = await rpc.engine_newPayloadV1(executionPayloadV12A);
        Assert.That(newPayloadResult2A.Data.Status, Is.EqualTo(PayloadStatus.Syncing));

        // Fork choice updated with unknown parent hash
        ForkchoiceStateV1 forkChoiceState2A = new(executionPayloadV12A.BlockHash,
            executionPayloadV12A.BlockHash,
            executionPayloadV12A.BlockHash);
        ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult2A = await rpc.engine_forkchoiceUpdatedV1(forkChoiceState2A);
        Assert.That(forkchoiceUpdatedResult2A.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Syncing));

        // New payload with correct parent hash
        ExecutionPayload executionPayloadV12B = CreateBlockRequest(chain, executionPayloadV11, TestItem.AddressA);
        ResultWrapper<PayloadStatusV1> newPayloadResult2B = await rpc.engine_newPayloadV1(executionPayloadV12B);
        Assert.That(newPayloadResult2B.Data.Status, Is.EqualTo(PayloadStatus.Valid));

        // Fork choice updated with correct parent hash
        ForkchoiceStateV1 forkChoiceState2B = new(executionPayloadV12B.BlockHash, executionPayloadV12B.BlockHash,
            executionPayloadV12B.BlockHash);
        ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult2B = await rpc.engine_forkchoiceUpdatedV1(forkChoiceState2B);
        Assert.That(forkchoiceUpdatedResult2B.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));

        // New payload unknown parent hash
        ExecutionPayload executionPayloadV13A = CreateBlockRequest(chain, executionPayloadV12A, TestItem.AddressA);
        ResultWrapper<PayloadStatusV1> newPayloadResult3A = await rpc.engine_newPayloadV1(executionPayloadV13A);
        Assert.That(newPayloadResult3A.Data.Status, Is.EqualTo(PayloadStatus.Syncing));

        // Fork choice updated with unknown parent hash
        ForkchoiceStateV1 forkChoiceState3A = new(executionPayloadV13A.BlockHash,
            executionPayloadV13A.BlockHash,
            executionPayloadV13A.BlockHash);
        ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult3A = await rpc.engine_forkchoiceUpdatedV1(forkChoiceState3A);
        Assert.That(forkchoiceUpdatedResult3A.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Syncing));

        ExecutionPayload executionPayloadV13B = CreateBlockRequest(chain, executionPayloadV12B, TestItem.AddressA);
        ResultWrapper<PayloadStatusV1> newPayloadResult3B = await rpc.engine_newPayloadV1(executionPayloadV13B);
        Assert.That(newPayloadResult3B.Data.Status, Is.EqualTo(PayloadStatus.Valid));

        // Fork choice updated with correct parent hash
        ForkchoiceStateV1 forkChoiceState3B = new(executionPayloadV13B.BlockHash, executionPayloadV13B.BlockHash,
            executionPayloadV13B.BlockHash);
        ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult3B = await rpc.engine_forkchoiceUpdatedV1(forkChoiceState3B);
        Assert.That(forkchoiceUpdatedResult3B.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
    }

    // Simulates sync flipping the canonical marker at the target's level without advancing Head
    // (wereProcessed:false skips head update). Produces the "stale canonical markers" scenario.
    private static void FlipCanonicalMarkerTo(MergeTestBlockchain chain, ExecutionPayload target)
    {
        Block targetBlock = chain.BlockTree.FindBlock(target.BlockHash, BlockTreeLookupOptions.None)!;
        chain.BlockTree.TryUpdateMainChain(targetBlock.Header, wereProcessed: false, preloadedBlocks: new[] { targetBlock });
    }

    // Y-shape: block1 -> {block2A (sibling), block2B -> block3B}, with head advanced to block1 via FCU.
    private static async Task<(ExecutionPayload Block1, ExecutionPayload Block2A, ExecutionPayload Block2B, ExecutionPayload Block3B)>
        BuildYShapedChainV1(MergeTestBlockchain chain, IEngineRpcModule rpc)
    {
        ExecutionPayload block1 = CreateBlockRequest(chain, CreateParentBlockRequestOnHead(chain.BlockTree), TestItem.AddressA);
        Assert.That((await rpc.engine_newPayloadV1(block1)).Data.Status, Is.EqualTo(PayloadStatus.Valid));

        ForkchoiceStateV1 fcu1 = new(block1.BlockHash, block1.BlockHash, block1.BlockHash);
        Assert.That((await rpc.engine_forkchoiceUpdatedV1(fcu1)).Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));

        ExecutionPayload block2A = CreateBlockRequest(chain, block1, TestItem.AddressB);
        Assert.That((await rpc.engine_newPayloadV1(block2A)).Data.Status, Is.EqualTo(PayloadStatus.Valid));

        ExecutionPayload block2B = CreateBlockRequest(chain, block1, TestItem.AddressA);
        Assert.That((await rpc.engine_newPayloadV1(block2B)).Data.Status, Is.EqualTo(PayloadStatus.Valid));

        ExecutionPayload block3B = CreateBlockRequest(chain, block2B, TestItem.AddressA);
        Assert.That((await rpc.engine_newPayloadV1(block3B)).Data.Status, Is.EqualTo(PayloadStatus.Valid));

        return (block1, block2A, block2B, block3B);
    }

    // Snapshots head/finalized/safe, sends an FCU that must be rejected as InvalidForkchoiceState,
    // and asserts the three pointers are unchanged.
    private static async Task AssertFcuRejectedAndStateUnchanged(MergeTestBlockchain chain, IEngineRpcModule rpc, ForkchoiceStateV1 fcu)
    {
        Hash256 initialHeadHash = chain.BlockFinder.HeadHash;
        Hash256 initialFinalizedHash = chain.BlockFinder.FinalizedHash!;
        Hash256 initialSafeHash = chain.BlockFinder.SafeHash!;

        ResultWrapper<ForkchoiceUpdatedV1Result> result = await rpc.engine_forkchoiceUpdatedV1(fcu);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ErrorCode, Is.EqualTo(MergeErrorCodes.InvalidForkchoiceState));

            Assert.That(chain.BlockTree.Head!.Hash, Is.EqualTo(initialHeadHash));
            Assert.That(chain.BlockFinder.HeadHash, Is.EqualTo(initialHeadHash));
            Assert.That(chain.BlockFinder.FinalizedHash, Is.EqualTo(initialFinalizedHash));
            Assert.That(chain.BlockFinder.SafeHash, Is.EqualTo(initialSafeHash));
        }
    }

    [TestCase(false, TestName = "inconsistent_finalized_hash")]
    [TestCase(true, TestName = "inconsistent_safe_hash")]
    public async Task inconsistent_sibling_hash_is_rejected(bool viaSafe)
    {
        using MergeTestBlockchain chain =
            await CreateBlockchain(null, new MergeConfig() { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;

        (ExecutionPayload block1, ExecutionPayload block2A, _, ExecutionPayload block3B) = await BuildYShapedChainV1(chain, rpc);

        // block2A is a sibling of branch B; passing it as either finalized or safe while head is on
        // branch B is not an ancestor relationship and must be rejected.
        ForkchoiceStateV1 fcu = viaSafe
            ? new(headBlockHash: block3B.BlockHash, finalizedBlockHash: block3B.BlockHash, safeBlockHash: block2A.BlockHash)
            : new(headBlockHash: block3B.BlockHash, finalizedBlockHash: block2A.BlockHash, safeBlockHash: block3B.BlockHash);
        await AssertFcuRejectedAndStateUnchanged(chain, rpc, fcu);

        Assert.That(chain.BlockTree.IsMainChain(block1.BlockHash), Is.True);
        Assert.That(chain.BlockTree.IsMainChain(block3B.BlockHash), Is.False);
    }

    [Test]
    public async Task inconsistent_safe_hash_is_rejected_when_head_is_ancestor_of_latest_known_finalized()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(null, new MergeConfig() { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;

        IReadOnlyList<ExecutionPayload> blocks = await ProduceBranchV1(
            rpc,
            chain,
            3,
            CreateParentBlockRequestOnHead(chain.BlockTree),
            setHead: true);

        ExecutionPayload block1 = blocks[0];
        ExecutionPayload block3 = blocks[2];
        Assert.That(chain.BlockFinder.HeadHash, Is.EqualTo(block3.BlockHash));
        Assert.That(chain.BlockFinder.FinalizedHash, Is.EqualTo(block3.BlockHash));

        // Old-head skip is optional, but InvalidForkchoiceState for an out-of-chain safe block is mandatory.
        ForkchoiceStateV1 fcu = new(headBlockHash: block1.BlockHash, finalizedBlockHash: block1.BlockHash, safeBlockHash: block3.BlockHash);
        await AssertFcuRejectedAndStateUnchanged(chain, rpc, fcu);
    }

    [Test]
    public async Task forkchoiceUpdated_safe_block_that_is_real_ancestor_of_new_head_is_accepted()
    {
        // Spec acceptance case for #11185: an FCU whose safe/finalized are real ancestors of the
        // new head (but not yet on the EL's currently-canonical chain) must be accepted as Valid.
        using MergeTestBlockchain chain =
            await CreateBlockchain(null, new MergeConfig() { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;

        (ExecutionPayload block1, _, ExecutionPayload block2B, ExecutionPayload block3B) = await BuildYShapedChainV1(chain, rpc);

        Assert.That(chain.BlockTree.Head!.Hash, Is.EqualTo(block1.BlockHash));
        Assert.That(chain.BlockTree.IsMainChain(block2B.BlockHash), Is.False);
        Assert.That(chain.BlockTree.IsMainChain(block3B.BlockHash), Is.False);

        ForkchoiceStateV1 fcu = new(headBlockHash: block3B.BlockHash, finalizedBlockHash: block1.BlockHash, safeBlockHash: block2B.BlockHash);
        ResultWrapper<ForkchoiceUpdatedV1Result> result = await rpc.engine_forkchoiceUpdatedV1(fcu);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ErrorCode, Is.EqualTo(0));
            Assert.That(result.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));

            Assert.That(chain.BlockTree.Head.Hash, Is.EqualTo(block3B.BlockHash));
            Assert.That(chain.BlockTree.IsMainChain(block3B.BlockHash), Is.True);
            Assert.That(chain.BlockTree.IsMainChain(block2B.BlockHash), Is.True);
            Assert.That(chain.BlockTree.IsMainChain(block1.BlockHash), Is.True);
        }
    }

    [Test]
    public async Task forkchoiceUpdated_accepts_safe_ancestor_when_head_is_main_but_ancestor_level_marker_is_stale()
    {
        // 1) Build X -> A and X -> B -> C, then set head=C with safe=B (valid ancestry).
        // 2) Force an inconsistent block-by-number view at level N so A is marked canonical while head remains C at N+1.
        // 3) Repeat FCU(head=C, safe=B, finalized=X).
        // Expected: VALID, because B is still on C's real parent path.
        using MergeTestBlockchain chain =
            await CreateBlockchain(null, new MergeConfig() { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;

        ExecutionPayload blockX = CreateBlockRequest(chain, CreateParentBlockRequestOnHead(chain.BlockTree), TestItem.AddressA);
        Assert.That((await rpc.engine_newPayloadV1(blockX)).Data.Status, Is.EqualTo(PayloadStatus.Valid));
        Assert.That(
            (await rpc.engine_forkchoiceUpdatedV1(new(blockX.BlockHash, blockX.BlockHash, blockX.BlockHash))).Data.PayloadStatus.Status,
            Is.EqualTo(PayloadStatus.Valid));

        ExecutionPayload blockA = CreateBlockRequest(chain, blockX, TestItem.AddressA);
        Assert.That((await rpc.engine_newPayloadV1(blockA)).Data.Status, Is.EqualTo(PayloadStatus.Valid));
        Assert.That(
            (await rpc.engine_forkchoiceUpdatedV1(new(blockA.BlockHash, blockX.BlockHash, blockX.BlockHash))).Data.PayloadStatus.Status,
            Is.EqualTo(PayloadStatus.Valid));

        ExecutionPayload blockB = CreateBlockRequest(chain, blockX, TestItem.AddressB);
        Assert.That((await rpc.engine_newPayloadV1(blockB)).Data.Status, Is.EqualTo(PayloadStatus.Valid));
        ExecutionPayload blockC = CreateBlockRequest(chain, blockB, TestItem.AddressB);
        Assert.That((await rpc.engine_newPayloadV1(blockC)).Data.Status, Is.EqualTo(PayloadStatus.Valid));

        ForkchoiceStateV1 reorgToC = new(headBlockHash: blockC.BlockHash, finalizedBlockHash: blockX.BlockHash, safeBlockHash: blockB.BlockHash);
        Assert.That((await rpc.engine_forkchoiceUpdatedV1(reorgToC)).Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));

        Block blockAInTree = chain.BlockTree.FindBlock(blockA.BlockHash, BlockTreeLookupOptions.None)!;
        Block blockCInTree = chain.BlockTree.FindBlock(blockC.BlockHash, BlockTreeLookupOptions.None)!;

        // Deliberately create stale canonical markers: level N -> A, level N+1 -> C.
        // ForceMainChainForTest moves exactly the given block (no connectivity walk), which is required to
        // stage this inconsistency - TryUpdateMainChain would walk C back through B and repair the marker.
        chain.BlockTree.ForceMainChainForTest(new[] { blockAInTree }, wereProcessed: true);
        chain.BlockTree.ForceMainChainForTest(new[] { blockCInTree }, wereProcessed: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.BlockTree.Head!.Hash, Is.EqualTo(blockC.BlockHash));
            Assert.That(chain.BlockTree.IsMainChain(blockC.BlockHash), Is.True, "precondition: head level marker points at C");
            Assert.That(chain.BlockTree.IsMainChain(blockA.BlockHash), Is.True, "precondition: stale marker points at A on C's parent level");
            Assert.That(chain.BlockTree.IsMainChain(blockB.BlockHash), Is.False, "precondition: true safe ancestor B is off-main only due stale marker");
        }

        ForkchoiceStateV1 repeated = new(headBlockHash: blockC.BlockHash, finalizedBlockHash: blockX.BlockHash, safeBlockHash: blockB.BlockHash);
        ResultWrapper<ForkchoiceUpdatedV1Result> result = await rpc.engine_forkchoiceUpdatedV1(repeated);

        // Must accept by ancestry (B -> C), even if B is temporarily off-main in level markers.
        Assert.That(result.ErrorCode, Is.EqualTo(0));
        Assert.That(result.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
    }

    [Test]
    public async Task forkchoiceUpdated_isInconsistent_takes_fast_path_when_candidate_is_on_main_chain()
    {
        // Coverage for the candidateIsMain/headNotMain branch of IsInconsistent: stale canonical
        // markers leave head=a3 off-main while a1/a2 remain on main. The repeated FCU keeps
        // shouldUpdateHead=false, so IsInconsistent actually walks. The optimized walk must stop
        // at the first main-chain ancestor (a2) instead of continuing all the way down to a1.
        BlockTreeCallSpy? spy = null;
        using MergeTestBlockchain chain = await CreateBlockchain(
            null,
            new MergeConfig() { TerminalTotalDifficulty = "0" },
            configurer: builder => builder.AddDecorator<IBlockTree>((_, inner) =>
            {
                (IBlockTree proxy, BlockTreeCallSpy created) = BlockTreeCallSpy.Wrap(inner);
                spy = created;
                return proxy;
            }));
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Assert.That(spy!, Is.Not.Null);

        ExecutionPayload a1 = CreateBlockRequest(chain, CreateParentBlockRequestOnHead(chain.BlockTree), TestItem.AddressA);
        Assert.That((await rpc.engine_newPayloadV1(a1)).Data.Status, Is.EqualTo(PayloadStatus.Valid));
        ExecutionPayload a2 = CreateBlockRequest(chain, a1, TestItem.AddressA);
        Assert.That((await rpc.engine_newPayloadV1(a2)).Data.Status, Is.EqualTo(PayloadStatus.Valid));
        ExecutionPayload a3 = CreateBlockRequest(chain, a2, TestItem.AddressA);
        Assert.That((await rpc.engine_newPayloadV1(a3)).Data.Status, Is.EqualTo(PayloadStatus.Valid));

        ForkchoiceStateV1 advance = new(headBlockHash: a3.BlockHash, finalizedBlockHash: a1.BlockHash, safeBlockHash: a2.BlockHash);
        Assert.That((await rpc.engine_forkchoiceUpdatedV1(advance)).Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));

        ExecutionPayload b3 = CreateBlockRequest(chain, a2, TestItem.AddressB);
        Assert.That((await rpc.engine_newPayloadV1(b3)).Data.Status, Is.EqualTo(PayloadStatus.Valid));
        FlipCanonicalMarkerTo(chain, b3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.BlockTree.Head!.Hash, Is.EqualTo(a3.BlockHash));
            Assert.That(chain.BlockTree.IsMainChain(a1.BlockHash), Is.True, "precondition: a1 stays on main at H=1");
            Assert.That(chain.BlockTree.IsMainChain(a2.BlockHash), Is.True, "precondition: a2 stays on main at H=2");
            Assert.That(chain.BlockTree.IsMainChain(a3.BlockHash), Is.False, "precondition: a3's marker was flipped to b3");
        }

        // Count FindHeader calls made by the repeated FCU only. Safe=Keccak.Zero skips its
        // ValidateBlockHash lookup. Baseline: 1 to resolve head, 1 for finalized validation,
        // 1 for IsOnMainChainBehindFinalized (FindFinalizedHeader), plus the IsInconsistent walk
        // (1 under the optimization, 2 without).
        spy!.ResetCounters();
        ForkchoiceStateV1 repeated = new(headBlockHash: a3.BlockHash, finalizedBlockHash: a1.BlockHash, safeBlockHash: Keccak.Zero);
        ResultWrapper<ForkchoiceUpdatedV1Result> result = await rpc.engine_forkchoiceUpdatedV1(repeated);
        Assert.That(result.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));

        Assert.That(spy.FindHeaderCalls, Is.EqualTo(4), "walk must stop at the first main-chain ancestor (a2) rather than continue to a1");
    }

    [Test]
    public async Task forkchoiceUpdated_safe_block_that_is_real_ancestor_of_current_head_is_accepted_when_canonical_markers_are_stale()
    {
        // Strong regression for #11185: keep Head at a2, then simulate sync moving the canonical
        // marker at H=1 to sibling b1 without advancing Head. a1 is still a real ancestor of a2
        // via parent pointers, but the old single-hash IsMainChain(a1) check would reject it.
        using MergeTestBlockchain chain =
            await CreateBlockchain(null, new MergeConfig() { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;

        ExecutionPayload a1 = CreateBlockRequest(
            chain, CreateParentBlockRequestOnHead(chain.BlockTree),
            TestItem.AddressA);
        Assert.That((await rpc.engine_newPayloadV1(a1)).Data.Status, Is.EqualTo(PayloadStatus.Valid));

        ExecutionPayload a2 = CreateBlockRequest(chain, a1, TestItem.AddressA);
        Assert.That((await rpc.engine_newPayloadV1(a2)).Data.Status, Is.EqualTo(PayloadStatus.Valid));

        ForkchoiceStateV1 initialFcu = new(headBlockHash: a2.BlockHash, finalizedBlockHash: Keccak.Zero, safeBlockHash: a1.BlockHash);
        Assert.That((await rpc.engine_forkchoiceUpdatedV1(initialFcu)).Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));

        Block genesis = chain.BlockTree.FindBlock(chain.BlockTree.GenesisHash, BlockTreeLookupOptions.None)!;
        ExecutionPayload genesisPayload = new()
        {
            BlockNumber = genesis.Number,
            BlockHash = genesis.Hash!,
            StateRoot = genesis.StateRoot!,
            ReceiptsRoot = genesis.ReceiptsRoot!,
            GasLimit = genesis.GasLimit,
            Timestamp = genesis.Timestamp,
            BaseFeePerGas = genesis.BaseFeePerGas,
        };

        ExecutionPayload b1 = CreateBlockRequest(chain, genesisPayload, TestItem.AddressB);
        Assert.That((await rpc.engine_newPayloadV1(b1)).Data.Status, Is.EqualTo(PayloadStatus.Valid));

        FlipCanonicalMarkerTo(chain, b1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.BlockTree.Head!.Hash, Is.EqualTo(a2.BlockHash), "Head stays on a2 when sync marks b1 canonical");
            Assert.That(chain.BlockTree.IsMainChain(a1.BlockHash), Is.False, "precondition: a1 is no longer canonical at H=1");
            Assert.That(chain.BlockTree.IsMainChain(a2.BlockHash), Is.False, "precondition: a2 marker was cleared above the sync target");
            Assert.That(chain.BlockTree.IsMainChain(b1.BlockHash), Is.True, "precondition: b1 became canonical at H=1");
        }

        ForkchoiceStateV1 repeatedHeadFcu = new(headBlockHash: a2.BlockHash, finalizedBlockHash: Keccak.Zero, safeBlockHash: a1.BlockHash);
        ResultWrapper<ForkchoiceUpdatedV1Result> result = await rpc.engine_forkchoiceUpdatedV1(repeatedHeadFcu);

        Assert.That(result.ErrorCode, Is.EqualTo(0));
        Assert.That(result.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
    }

    private async Task<IReadOnlyList<ExecutionPayload>> BuildChainWithLoweredFinalized(
        MergeTestBlockchain chain, IEngineRpcModule rpc, int oldHead, int lastFinalized)
    {
        IReadOnlyList<ExecutionPayload> blocks = await ProduceBranchV1(rpc, chain, oldHead + 1, CreateParentBlockRequestOnHead(chain.BlockTree), setHead: true);

        // Lower the finalized marker to blocks[lastFinalized] while keeping the head at blocks[oldHead].
        Hash256 finalized = blocks[lastFinalized].BlockHash;
        ForkchoiceStateV1 setFinalized = new(headBlockHash: blocks[oldHead].BlockHash, finalizedBlockHash: finalized, safeBlockHash: finalized);
        Assert.That((await rpc.engine_forkchoiceUpdatedV1(setFinalized)).Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
        Assert.That(chain.BlockTree.Head!.Hash, Is.EqualTo(blocks[oldHead].BlockHash));
        return blocks;
    }

    [TestCase(-1, TestName = "Processed behind finalized")]
    [TestCase(0, TestName = "Processed last finalized")]
    [TestCase(1, TestName = "Processed after finalized")]
    public async Task forkchoiceUpdatedV1_processed_skips_reorg_only_when_head_is_ancestor_of_finalized(int offset)
    {
        using MergeTestBlockchain chain =
            await CreateBlockchain(null, new MergeConfig() { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;

        const int oldHead = 4;
        const int lastFinalized = 2;
        IReadOnlyList<ExecutionPayload> blocks = await BuildChainWithLoweredFinalized(chain, rpc, oldHead, lastFinalized);

        // Zero request-level finalized/safe so RejectIfInconsistent (which runs before the skip
        // check) does not reject the offset < 0 case where finalized > head. The skip check still
        // fires via the BlockTree's internal FinalizedHash set by the helper.
        int newHead = lastFinalized + offset;
        ForkchoiceStateV1 fcu = new(headBlockHash: blocks[newHead].BlockHash, finalizedBlockHash: Keccak.Zero, safeBlockHash: Keccak.Zero);
        ResultWrapper<ForkchoiceUpdatedV1Result> result = await rpc.engine_forkchoiceUpdatedV1(fcu);
        Assert.That(result.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));

        if (offset < 0)
        {
            // Skip path: the FCU returns Valid without reorging; the head stays at blocks[oldHead].
            Assert.That(chain.BlockTree.Head!.Hash, Is.EqualTo(blocks[oldHead].BlockHash));
        }
        else
        {
            // No skip: the regular reorg path runs and the head is updated to blocks[newHead].
            Assert.That(chain.BlockTree.Head!.Hash, Is.EqualTo(blocks[newHead].BlockHash));
        }
    }

    [TestCase(-1, TestName = "Unprocessed behind finalized")]
    [TestCase(0, TestName = "Unprocessed last finalized")]
    [TestCase(1, TestName = "Unprocessed after finalized")]
    public async Task forkchoiceUpdatedV1_unprocessed_skips_reorg_only_when_head_is_ancestor_of_finalized(int offset)
    {
        using MergeTestBlockchain chain =
            await CreateBlockchain(null, new MergeConfig() { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;

        const int oldHead = 4;
        const int lastFinalized = 2;
        IReadOnlyList<ExecutionPayload> blocks = await BuildChainWithLoweredFinalized(chain, rpc, oldHead, lastFinalized);
        Hash256 finalized = blocks[lastFinalized].BlockHash;

        int newHead = lastFinalized + offset;
        // Reset the candidate's WasProcessed flag (the block stays on the main chain) so the
        // FCU enters the unprocessed branch where the first skip check lives.
        FlipCanonicalMarkerTo(chain, blocks[newHead]);

        ForkchoiceStateV1 fcu = new(headBlockHash: blocks[newHead].BlockHash, finalizedBlockHash: finalized, safeBlockHash: finalized);
        ResultWrapper<ForkchoiceUpdatedV1Result> result = await rpc.engine_forkchoiceUpdatedV1(fcu);

        if (offset < 0)
        {
            // Skip path: the unprocessed branch returns Valid early without falling through
            // to the sync logic.
            Assert.That(result.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
        }
        else
        {
            // No skip: the unprocessed branch falls through and returns Syncing.
            Assert.That(result.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Syncing));
        }
    }

    [Test]
    public async Task forkchoiceUpdated_accepts_lower_finalized_than_previous_but_rejects_safe_before_finalized()
    {
        using MergeTestBlockchain chain =
            await CreateBlockchain(null, new MergeConfig() { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;

        IReadOnlyList<ExecutionPayload> blocks = await ProduceBranchV1(rpc, chain, 4, CreateParentBlockRequestOnHead(chain.BlockTree), setHead: true);

        ForkchoiceStateV1 higherFinalized = new(headBlockHash: blocks[3].BlockHash, finalizedBlockHash: blocks[2].BlockHash, safeBlockHash: blocks[2].BlockHash);
        ResultWrapper<ForkchoiceUpdatedV1Result> higherFinalizedResult = await rpc.engine_forkchoiceUpdatedV1(higherFinalized);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(higherFinalizedResult.ErrorCode, Is.EqualTo(0));
            Assert.That(higherFinalizedResult.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(chain.BlockTree.FinalizedHash, Is.EqualTo(blocks[2].BlockHash));
            Assert.That(chain.BlockTree.LastFinalizedBlockLevel, Is.EqualTo(blocks[2].BlockNumber));
        }

        ForkchoiceStateV1 lowerFinalized = new(headBlockHash: blocks[3].BlockHash, finalizedBlockHash: blocks[1].BlockHash, safeBlockHash: blocks[2].BlockHash);
        ResultWrapper<ForkchoiceUpdatedV1Result> lowerFinalizedResult = await rpc.engine_forkchoiceUpdatedV1(lowerFinalized);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(lowerFinalizedResult.ErrorCode, Is.EqualTo(0));
            Assert.That(lowerFinalizedResult.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(chain.BlockTree.FinalizedHash, Is.EqualTo(blocks[1].BlockHash));
            Assert.That(chain.BlockTree.LastFinalizedBlockLevel, Is.EqualTo(blocks[1].BlockNumber));
        }

        // Request-local spec ordering: safe must be at or after finalized.
        ForkchoiceStateV1 ordering = new(headBlockHash: blocks[3].BlockHash, finalizedBlockHash: blocks[2].BlockHash, safeBlockHash: blocks[1].BlockHash);
        Assert.That((await rpc.engine_forkchoiceUpdatedV1(ordering)).ErrorCode, Is.EqualTo(MergeErrorCodes.InvalidForkchoiceState));
    }

    [Test]
    public async Task forkchoiceUpdatedV1_should_allow_lower_finalized_than_previous_when_building_payload()
    {
        using MergeTestBlockchain chain =
            await CreateBlockchain(null, new MergeConfig() { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;

        IReadOnlyList<ExecutionPayload> blocks = await ProduceBranchV1(rpc, chain, 4, CreateParentBlockRequestOnHead(chain.BlockTree), setHead: true);

        PayloadAttributes payloadAttributes = new()
        {
            Timestamp = blocks[3].Timestamp + 1,
            PrevRandao = TestItem.KeccakB,
            SuggestedFeeRecipient = TestItem.AddressC,
        };

        ForkchoiceStateV1 higherFinalized = new(headBlockHash: blocks[3].BlockHash, finalizedBlockHash: blocks[2].BlockHash, safeBlockHash: blocks[2].BlockHash);
        ResultWrapper<ForkchoiceUpdatedV1Result> higherFinalizedResult = await rpc.engine_forkchoiceUpdatedV1(higherFinalized);
        Assert.That(higherFinalizedResult.ErrorCode, Is.EqualTo(0));
        Assert.That(higherFinalizedResult.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));

        ForkchoiceStateV1 repeatedHead = new(headBlockHash: blocks[3].BlockHash, finalizedBlockHash: blocks[1].BlockHash, safeBlockHash: blocks[2].BlockHash);
        ResultWrapper<ForkchoiceUpdatedV1Result> result = await rpc.engine_forkchoiceUpdatedV1(repeatedHead, payloadAttributes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ErrorCode, Is.EqualTo(0));
            Assert.That(result.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(result.Data.PayloadId, Is.Not.Null);
        }
    }

    [TestCase(false, TestName = "forkchoiceUpdated_rejects_repeated_finalized_when_head_on_sibling_branch")]
    [TestCase(true, TestName = "forkchoiceUpdated_rejects_repeated_safe_when_head_on_sibling_branch")]
    public async Task forkchoiceUpdated_rejects_repeated_hash_when_head_on_sibling_branch(bool cachedSafe)
    {
        // A repeated finalized/safe hash must not bypass ancestry validation against the requested head.
        // The binding is (head, finalized, safe); if the head moves to a sibling branch that is not a
        // descendant of the previously-accepted hash, the FCU must be rejected.
        using MergeTestBlockchain chain =
            await CreateBlockchain(null, new MergeConfig() { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;

        // Common ancestor c1, then two branches diverge: c1 -> a1/b1 -> a2/b2
        ExecutionPayload c1 = CreateBlockRequest(chain, CreateParentBlockRequestOnHead(chain.BlockTree), TestItem.AddressA);
        Assert.That((await rpc.engine_newPayloadV1(c1)).Data.Status, Is.EqualTo(PayloadStatus.Valid));

        ExecutionPayload a1 = CreateBlockRequest(chain, c1, TestItem.AddressA);
        Assert.That((await rpc.engine_newPayloadV1(a1)).Data.Status, Is.EqualTo(PayloadStatus.Valid));
        ExecutionPayload a2 = CreateBlockRequest(chain, a1, TestItem.AddressA);
        Assert.That((await rpc.engine_newPayloadV1(a2)).Data.Status, Is.EqualTo(PayloadStatus.Valid));

        ExecutionPayload b1 = CreateBlockRequest(chain, c1, TestItem.AddressB);
        Assert.That((await rpc.engine_newPayloadV1(b1)).Data.Status, Is.EqualTo(PayloadStatus.Valid));
        ExecutionPayload b2 = CreateBlockRequest(chain, b1, TestItem.AddressB);
        Assert.That((await rpc.engine_newPayloadV1(b2)).Data.Status, Is.EqualTo(PayloadStatus.Valid));

        // FCU1 on branch A: cache either finalized=a1 or safe=a1 (a1 is NOT an ancestor of b2).
        ForkchoiceStateV1 fcu1 = cachedSafe
            ? new(headBlockHash: a2.BlockHash, finalizedBlockHash: c1.BlockHash, safeBlockHash: a1.BlockHash)
            : new(headBlockHash: a2.BlockHash, finalizedBlockHash: a1.BlockHash, safeBlockHash: a1.BlockHash);
        Assert.That((await rpc.engine_forkchoiceUpdatedV1(fcu1)).Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));

        // FCU2: head on branch B, reusing the cached a1 as either safe or finalized.
        // a1 is NOT an ancestor of b2; must be rejected regardless of caching.
        ForkchoiceStateV1 fcu2 = cachedSafe
            ? new(headBlockHash: b2.BlockHash, finalizedBlockHash: c1.BlockHash, safeBlockHash: a1.BlockHash)
            : new(headBlockHash: b2.BlockHash, finalizedBlockHash: a1.BlockHash, safeBlockHash: b1.BlockHash);
        Assert.That((await rpc.engine_forkchoiceUpdatedV1(fcu2)).ErrorCode, Is.EqualTo(MergeErrorCodes.InvalidForkchoiceState));
    }

    [Test]
    public async Task payloadV1_latest_block_after_reorg()
    {
        using MergeTestBlockchain chain =
            await CreateBlockchain(null, new MergeConfig() { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;

        Hash256 prevRandao1 = TestItem.KeccakA;
        Hash256 prevRandao2 = TestItem.KeccakB;
        Hash256 prevRandao3 = TestItem.KeccakC;

        {
            ForkchoiceStateV1 forkChoiceStateGen = new(chain.BlockTree.Head!.Hash!, chain.BlockTree.Head!.Hash!,
                chain.BlockTree.Head!.Hash!);
            ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResultGen =
                await rpc.engine_forkchoiceUpdatedV1(forkChoiceStateGen,
                    new PayloadAttributes()
                    {
                        Timestamp = Timestamper.UnixTime.Seconds,
                        PrevRandao = prevRandao1,
                        SuggestedFeeRecipient = Address.Zero
                    });
            Assert.That(forkchoiceUpdatedResultGen.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
        }

        // Add one block
        ExecutionPayload executionPayloadV11 = CreateBlockRequest(
            chain, CreateParentBlockRequestOnHead(chain.BlockTree),
            TestItem.AddressA);
        executionPayloadV11.PrevRandao = prevRandao1;

        TryCalculateHash(executionPayloadV11, out Hash256? hash1);
        executionPayloadV11.BlockHash = hash1;

        ResultWrapper<PayloadStatusV1> newPayloadResult1 = await rpc.engine_newPayloadV1(executionPayloadV11);
        Assert.That(newPayloadResult1.Data.Status, Is.EqualTo(PayloadStatus.Valid));

        ForkchoiceStateV1 forkChoiceState1 = new(executionPayloadV11.BlockHash,
            executionPayloadV11.BlockHash, executionPayloadV11.BlockHash);
        ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult1 =
            await rpc.engine_forkchoiceUpdatedV1(forkChoiceState1,
                new PayloadAttributes()
                {
                    Timestamp = Timestamper.UnixTime.Seconds,
                    PrevRandao = prevRandao2,
                    SuggestedFeeRecipient = Address.Zero
                });
        Assert.That(forkchoiceUpdatedResult1.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));


        {
            ExecutionPayload executionPayloadV12 = CreateBlockRequest(
                chain, executionPayloadV11,
                TestItem.AddressA);

            executionPayloadV12.PrevRandao = prevRandao3;

            TryCalculateHash(executionPayloadV12, out Hash256? hash);
            executionPayloadV12.BlockHash = hash;

            ResultWrapper<PayloadStatusV1> newPayloadResult2 = await rpc.engine_newPayloadV1(executionPayloadV12);
            Assert.That(newPayloadResult2.Data.Status, Is.EqualTo(PayloadStatus.Valid));

            ForkchoiceStateV1 forkChoiceState2 = new(executionPayloadV12.BlockHash,
                executionPayloadV11.BlockHash, executionPayloadV11.BlockHash);
            ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult2 =
                await rpc.engine_forkchoiceUpdatedV1(forkChoiceState2);
            Assert.That(forkchoiceUpdatedResult2.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));

            Hash256 currentBlockHash = chain.BlockTree.Head!.Hash!;
            Assert.That(currentBlockHash == executionPayloadV12.BlockHash, Is.True);
        }

        // re-org
        {
            ExecutionPayload executionPayloadV13 = CreateBlockRequest(chain, executionPayloadV11, TestItem.AddressA);

            executionPayloadV13.PrevRandao = prevRandao2;

            TryCalculateHash(executionPayloadV13, out Hash256? hash);
            executionPayloadV13.BlockHash = hash;

            ResultWrapper<PayloadStatusV1> newPayloadResult3 = await rpc.engine_newPayloadV1(executionPayloadV13);
            Assert.That(newPayloadResult3.Data.Status, Is.EqualTo(PayloadStatus.Valid));

            ForkchoiceStateV1 forkChoiceState3 = new(executionPayloadV13.BlockHash,
                executionPayloadV11.BlockHash, executionPayloadV11.BlockHash);
            ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult3 =
                await rpc.engine_forkchoiceUpdatedV1(forkChoiceState3);
            Assert.That(forkchoiceUpdatedResult3.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));

            Hash256 currentBlockHash = chain.BlockTree.Head!.Hash!;
            Assert.That(currentBlockHash != forkChoiceState3.HeadBlockHash ||
                        currentBlockHash == forkChoiceState3.SafeBlockHash ||
                        currentBlockHash == forkChoiceState3.FinalizedBlockHash, Is.False);
        }
    }

    [Test]
    public async Task Should_return_ClientVersionV1()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpcModule = chain.EngineRpcModule;
        ResultWrapper<ClientVersionV1[]> result = rpcModule.engine_getClientVersionV1(default);
        Assert.That(result.Data, Is.EqualTo([new ClientVersionV1()]));
    }

    [Test]
    public async Task Should_return_capabilities()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);
        IEngineRpcModule rpcModule = chain.EngineRpcModule;
        IOrderedEnumerable<string> expected = typeof(IEngineRpcModule).GetMethods()
            .Select(static m => m.Name)
            .Where(static m => !m.Equals(nameof(IEngineRpcModule.engine_exchangeCapabilities), StringComparison.Ordinal)
                            && !m.Equals(nameof(IEngineRpcModule.engine_exchangeTransitionConfigurationV1), StringComparison.Ordinal))
            .Order();

        ResultWrapper<IReadOnlyList<string>> result = rpcModule.engine_exchangeCapabilities(expected);

        // The advertised list mixes JSON-RPC method names and SSZ-REST paths per spec.
        // Filter to JSON-RPC names by intersecting with reflection over IEngineRpcModule.
        HashSet<string> jsonRpcMethodNames = [.. typeof(IEngineRpcModule).GetMethods().Select(static m => m.Name)];
        Assert.That(result.Data.Where(jsonRpcMethodNames.Contains), Is.EquivalentTo(expected));
    }

    [Test]
    public void Should_return_expected_capabilities_for_mainnet()
    {
        ChainSpecFileLoader loader = new(new EthereumJsonSerializer(), LimboLogs.Instance);
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/foundation.json");
        ChainSpec chainSpec = loader.LoadEmbeddedOrFromFile(path);
        ChainSpecBasedSpecProvider specProvider = new(chainSpec);
        EngineRpcCapabilitiesProvider engineRpcCapabilitiesProvider = new(specProvider);
        string[] result = [.. engineRpcCapabilitiesProvider.GetJsonRpcCapabilities()
            .Where(kv => kv.Value.IsEnabled())
            .Select(kv => kv.Key)];
        string[] expectedMethods =
        [
            nameof(IEngineRpcModule.engine_getClientVersionV1),

            nameof(IEngineRpcModule.engine_getPayloadV1),
            nameof(IEngineRpcModule.engine_forkchoiceUpdatedV1),
            nameof(IEngineRpcModule.engine_newPayloadV1),

            nameof(IEngineRpcModule.engine_getPayloadV2),
            nameof(IEngineRpcModule.engine_forkchoiceUpdatedV2),
            nameof(IEngineRpcModule.engine_newPayloadV2),
            nameof(IEngineRpcModule.engine_getPayloadBodiesByHashV1),
            nameof(IEngineRpcModule.engine_getPayloadBodiesByRangeV1),

            nameof(IEngineRpcModule.engine_getPayloadV3),
            nameof(IEngineRpcModule.engine_forkchoiceUpdatedV3),
            nameof(IEngineRpcModule.engine_newPayloadV3),
            nameof(IEngineRpcModule.engine_getBlobsV1),

            nameof(IEngineRpcModule.engine_getPayloadV4),
            nameof(IEngineRpcModule.engine_newPayloadV4),

            nameof(IEngineRpcModule.engine_getPayloadV5),
            nameof(IEngineRpcModule.engine_getBlobsV2),
            nameof(IEngineRpcModule.engine_getBlobsV3),
            nameof(IEngineRpcModule.engine_getBlobsV4)
        ];
        Assert.That(result, Is.EquivalentTo(expectedMethods));
    }

    [Test]
    public async Task Should_warn_for_missing_capabilities()
    {
        ILogManager loggerManager = Substitute.For<ILogManager>();
        InterfaceLogger iLogger = Substitute.For<InterfaceLogger>();
        iLogger.IsWarn.Returns(true);
        ILogger logger = new(iLogger);
        loggerManager.GetClassLogger<ExchangeCapabilitiesHandler>().Returns(logger);

        using MergeTestBlockchain chain = await CreateBaseBlockchain()
            .BuildMergeTestBlockchain(configurer: builder => builder
                .AddSingleton<ISpecProvider>(new TestSingleReleaseSpecProvider(Prague.Instance))
                .AddSingleton<ILogManager>(loggerManager));

        IEngineRpcModule rpcModule = chain.EngineRpcModule;
        string[] list = new[]
        {
            nameof(IEngineRpcModule.engine_forkchoiceUpdatedV3),
            nameof(IEngineRpcModule.engine_newPayloadV3),
            nameof(IEngineRpcModule.engine_newPayloadV4),
            nameof(IEngineRpcModule.engine_getPayloadV3)
        };

        ResultWrapper<IReadOnlyList<string>> result = rpcModule.engine_exchangeCapabilities(list);

        chain.LogManager.GetClassLogger<ExchangeCapabilitiesHandler>().UnderlyingLogger.Received().Warn(
            Arg.Is<string>(static a =>
                a.Contains(nameof(IEngineRpcModule.engine_getPayloadV4), StringComparison.Ordinal)));
    }

    [Test]
    public async Task Should_not_warn_for_missing_ssz_rest_paths()
    {
        ILogManager loggerManager = Substitute.For<ILogManager>();
        InterfaceLogger iLogger = Substitute.For<InterfaceLogger>();
        iLogger.IsWarn.Returns(true);
        ILogger logger = new(iLogger);
        loggerManager.GetClassLogger<ExchangeCapabilitiesHandler>().Returns(logger);

        using MergeTestBlockchain chain = await CreateBaseBlockchain()
            .BuildMergeTestBlockchain(configurer: builder => builder
                .AddSingleton<ISpecProvider>(new TestSingleReleaseSpecProvider(Prague.Instance))
                .AddSingleton<ILogManager>(loggerManager));

        EngineRpcCapabilitiesProvider provider = new(new TestSingleReleaseSpecProvider(Prague.Instance));
        string[] list = [.. provider.GetJsonRpcCapabilities().Where(kv => kv.Value.IsEnabled()).Select(kv => kv.Key)];

        chain.EngineRpcModule.engine_exchangeCapabilities(list);

        chain.LogManager.GetClassLogger<ExchangeCapabilitiesHandler>().UnderlyingLogger.DidNotReceive()
            .Warn(Arg.Any<string>());
    }

    private static readonly string[] SszRestPathsParis =
    [
        SszRestPaths.PostPayloads,
        SszRestPaths.GetPayloads,
        SszRestPaths.PostForkchoice,
        SszRestPaths.GetCapabilities,
        SszRestPaths.GetIdentity,
    ];

    private static readonly string[] SszRestPathsShanghai =
    [
        SszRestPaths.PostBodiesByHash,
        SszRestPaths.GetBodiesByRange,
    ];

    private static readonly string[] SszRestPathsCancun =
    [
        SszRestPaths.PostBlobsV1,
    ];

    // Prague adds new method versions (newPayloadV4/getPayloadV4) but no new REST path.
    private static readonly string[] SszRestPathsPrague = [];

    private static readonly string[] SszRestPathsOsaka =
    [
        SszRestPaths.PostBlobsV2,
        SszRestPaths.PostBlobsV3,
        SszRestPaths.PostBlobsV4,
    ];

    // Amsterdam adds new method versions (newPayloadV5/getPayloadV6/fcuV4/bodies V2) at existing paths;
    // the only genuinely new path is the witness endpoint.
    private static readonly string[] SszRestPathsAmsterdam =
    [
        SszRestPaths.PostPayloadsWitness,
    ];

    public static IEnumerable<TestCaseData> SszRestPathsAdvertisedCases()
    {
        yield return new TestCaseData(
            Osaka.Instance,
            (string[])[.. SszRestPathsParis, .. SszRestPathsShanghai, .. SszRestPathsCancun, .. SszRestPathsPrague, .. SszRestPathsOsaka])
            .SetName(nameof(SszRestPathsAreAdvertised) + "_for_Osaka");

        yield return new TestCaseData(
            Amsterdam.Instance,
            (string[])[.. SszRestPathsParis, .. SszRestPathsShanghai, .. SszRestPathsCancun, .. SszRestPathsPrague, .. SszRestPathsOsaka, .. SszRestPathsAmsterdam])
            .SetName(nameof(SszRestPathsAreAdvertised) + "_for_Amsterdam");
    }

    [TestCaseSource(nameof(SszRestPathsAdvertisedCases))]
    public void SszRestPathsAreAdvertised(IReleaseSpec releaseSpec, string[] expectedPaths)
    {
        ISpecProvider specProvider = new TestSingleReleaseSpecProvider(releaseSpec);
        EngineRpcCapabilitiesProvider engineRpcCapabilitiesProvider = new(specProvider);

        string[] result = [.. engineRpcCapabilitiesProvider.GetSszRestPaths()
            .Where(kv => kv.Value.IsEnabled())
            .Select(kv => kv.Key)];

        Assert.That(result, Is.EquivalentTo(expectedPaths));
    }

    private async Task<ExecutionPayload> BuildAndGetPayloadResult(
        IEngineRpcModule rpc, MergeTestBlockchain chain, Hash256 headBlockHash, Hash256 finalizedBlockHash,
        Hash256 safeBlockHash,
        ulong timestamp, Hash256 random, Address feeRecipient, bool waitForBlockImprovement = true)
    {
        Task blockImprovementWait = waitForBlockImprovement
            ? chain.WaitForImprovedBlock()
            : Task.CompletedTask;

        ForkchoiceStateV1 forkchoiceState = new(headBlockHash, finalizedBlockHash, safeBlockHash);
        PayloadAttributes payloadAttributes =
            new() { Timestamp = timestamp, PrevRandao = random, SuggestedFeeRecipient = feeRecipient };
        string payloadId = rpc.engine_forkchoiceUpdatedV1(forkchoiceState, payloadAttributes).Result.Data.PayloadId!;
        await blockImprovementWait;
        ResultWrapper<ExecutionPayload?> getPayloadResult =
            await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId));
        return getPayloadResult.Data!;
    }

    private async Task<ExecutionPayload> BuildAndGetPayloadResult(MergeTestBlockchain chain,
        IEngineRpcModule rpc, PayloadAttributes payloadAttributes)
    {
        Hash256 startingHead = chain.BlockTree.HeadHash;
        Hash256 parentHead = chain.BlockTree.Head!.ParentHash!;

        return await BuildAndGetPayloadResult(rpc, chain, startingHead, parentHead, startingHead,
            payloadAttributes.Timestamp, payloadAttributes.PrevRandao!, payloadAttributes.SuggestedFeeRecipient!);
    }

    private async Task<ExecutionPayload> BuildAndGetPayloadResult(MergeTestBlockchain chain,
        IEngineRpcModule rpc)
    {
        Hash256 startingHead = chain.BlockTree.HeadHash;
        Hash256 parentHead = chain.BlockTree.Head!.ParentHash!;

        ulong timestamp = Timestamper.UnixTime.Seconds;
        Hash256 random = Keccak.Zero;
        Address feeRecipient = Address.Zero;

        return await BuildAndGetPayloadResult(rpc, chain, startingHead, parentHead, startingHead,
            timestamp, random, feeRecipient);
    }

    public static IEnumerable<TestCaseData> ForkchoiceUpdatedFieldValidationTestCases
    {
        get
        {
            static PayloadAttributes Attrs(
                Withdrawal[]? withdrawals = null,
                Hash256? parentBeaconBlockRoot = null,
                ulong? slotNumber = null,
                Action<PayloadAttributes>? mutate = null)
            {
                PayloadAttributes attrs = new()
                {
                    Timestamp = 1,
                    PrevRandao = Keccak.Zero,
                    SuggestedFeeRecipient = Address.Zero,
                    Withdrawals = withdrawals,
                    ParentBeaconBlockRoot = parentBeaconBlockRoot,
                    SlotNumber = slotNumber,
                };
                mutate?.Invoke(attrs);
                return attrs;
            }

            static TestCaseData InvalidFieldCase(IReleaseSpec spec, string method, PayloadAttributes attrs, string testName) =>
                new(spec, method, attrs)
                {
                    TestName = testName,
                    ExpectedResult = MergeErrorCodes.InvalidPayloadAttributes,
                };

            yield return InvalidFieldCase(Paris.Instance, nameof(IEngineRpcModule.engine_forkchoiceUpdatedV1), Attrs(mutate: a => a.Timestamp = 0), "FCUv1 Timestamp zero");
            yield return InvalidFieldCase(Paris.Instance, nameof(IEngineRpcModule.engine_forkchoiceUpdatedV1), Attrs(mutate: a => a.PrevRandao = null), "FCUv1 PrevRandao null");
            yield return InvalidFieldCase(Paris.Instance, nameof(IEngineRpcModule.engine_forkchoiceUpdatedV1), Attrs(mutate: a => a.SuggestedFeeRecipient = null), "FCUv1 SuggestedFeeRecipient null");

            yield return InvalidFieldCase(Cancun.Instance, nameof(IEngineRpcModule.engine_forkchoiceUpdatedV3), Attrs(parentBeaconBlockRoot: Keccak.Zero), "FCUv3 Withdrawals null");
            yield return InvalidFieldCase(Amsterdam.Instance, nameof(IEngineRpcModule.engine_forkchoiceUpdatedV4), Attrs(parentBeaconBlockRoot: Keccak.Zero, slotNumber: 1), "FCUv4 Withdrawals null");
            yield return InvalidFieldCase(Amsterdam.Instance, nameof(IEngineRpcModule.engine_forkchoiceUpdatedV4), Attrs(withdrawals: [], slotNumber: 1), "FCUv4 ParentBeaconBlockRoot null");
        }
    }

    [TestCaseSource(nameof(ForkchoiceUpdatedFieldValidationTestCases))]
    public async Task<int> ForkchoiceUpdated_should_validate_payload_attributes_fields(
        IReleaseSpec releaseSpec, string method, PayloadAttributes payloadAttributes)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: releaseSpec);
        IEngineRpcModule rpcModule = chain.EngineRpcModule;
        ForkchoiceStateV1 fcuState = new(chain.BlockTree.HeadHash, chain.BlockTree.HeadHash, chain.BlockTree.HeadHash);

        // Set a valid timestamp relative to the chain head if test case left it non-zero
        if (payloadAttributes.Timestamp != 0)
            payloadAttributes.Timestamp = chain.BlockTree.Head!.Timestamp + 1;

        string response = await RpcTest.TestSerializedRequest(rpcModule, method,
            chain.JsonSerializer.Serialize(fcuState),
            chain.JsonSerializer.Serialize(payloadAttributes));
        JsonRpcErrorResponse errorResponse = chain.JsonSerializer.Deserialize<JsonRpcErrorResponse>(response);

        return errorResponse.Error?.Code ?? ErrorCodes.None;
    }

    protected virtual BlockBuilder BuildNewBlock(Block head)
        => Build.A.Block.WithNumber(head.Number)
            .WithParent(head)
            .WithNonce(0)
            .WithDifficulty(1000000)
            .WithTotalDifficulty(2000000L)
            .WithStateRoot(new Hash256("0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f"));

    protected virtual BlockBuilder BuildOneMoreTerminalBlock(Block head, bool correctStateRoot = true)
        => Build.A.Block.WithNumber(head.Number)
            .WithParent(head)
            .WithNonce(0)
            .WithDifficulty(900000)
            .WithTotalDifficulty(1900000L)
            .WithStateRoot(new Hash256(correctStateRoot ? "0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f" : "0x1ef7300d8961797263939a3d29bfba4ccf1702fabf02d8ad7a20b454edb6fd2f"));
}
