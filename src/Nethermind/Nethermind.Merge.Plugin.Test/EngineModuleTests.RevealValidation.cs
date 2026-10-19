// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [Test]
    public async Task NewPayloadV5_returns_invalid_for_gas_limit_outside_parent_bound([Values] bool tooHigh)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);
        BlockHeader parent = await ImportAmsterdamBlock(chain);
        (ExecutionPayloadV4 built, byte[][] requests) = await BuildAmsterdamPayload(chain);

        Block block = built.TryGetBlock().Data!;
        ulong bound = parent.GasLimit / 1024;
        block.Header.GasLimit = tooHigh ? parent.GasLimit + bound : parent.GasLimit - bound;
        block.Header.Hash = block.Header.CalculateHash();

        ResultWrapper<PayloadStatusV1> result = await chain.EngineRpcModule.engine_newPayloadV5(
            ExecutionPayloadV4.Create(block), [], TestItem.KeccakE, requests);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success), result.Result.Error);
            Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Invalid));
            Assert.That(result.Data.LatestValidHash, Is.EqualTo(parent.Hash));
            Assert.That(result.Data.ValidationError, Does.Contain("GasLimit"));
        }
    }

    [Test]
    public async Task NewPayloadV5_returns_invalid_for_builder_requests_the_block_does_not_produce(
        [Values((byte)ExecutionRequestType.BuilderDepositRequest, (byte)ExecutionRequestType.BuilderExitRequest)] byte requestType)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);
        BlockHeader parent = await ImportAmsterdamBlock(chain);
        (ExecutionPayloadV4 built, byte[][] produced) = await BuildAmsterdamPayload(chain);

        // A builder's envelope commits to requests through the header hash, so the payload is self-consistent.
        byte[][] claimed = [.. produced, [requestType, .. new byte[88]]];
        Block block = built.TryGetBlock().Data!;
        block.Header.RequestsHash = ExecutionRequestExtensions.CalculateHashFromFlatEncodedRequests(claimed);
        block.Header.Hash = block.Header.CalculateHash();

        ResultWrapper<PayloadStatusV1> result = await chain.EngineRpcModule.engine_newPayloadV5(
            ExecutionPayloadV4.Create(block), [], TestItem.KeccakE, claimed);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success), result.Result.Error);
            Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Invalid));
            Assert.That(result.Data.LatestValidHash, Is.EqualTo(parent.Hash));
            Assert.That(result.Data.ValidationError, Does.Contain("Requests hash mismatch"));
        }
    }

    /// <summary>Imports one post-merge block so a later verdict has a proof-of-stake ancestor to name.</summary>
    private static async Task<BlockHeader> ImportAmsterdamBlock(MergeTestBlockchain chain)
    {
        (ExecutionPayloadV4 payload, byte[][] requests) = await BuildAmsterdamPayload(chain);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Assert.That((await rpc.engine_newPayloadV5(payload, [], TestItem.KeccakE, requests)).Data.Status, Is.EqualTo(PayloadStatus.Valid));
        ForkchoiceStateV1 state = new(payload.BlockHash, payload.BlockHash, payload.BlockHash);
        Assert.That((await rpc.engine_forkchoiceUpdatedV4(state, null)).Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
        return chain.BlockTree.Head!.Header;
    }

    private static async Task<(ExecutionPayloadV4 Payload, byte[][] Requests)> BuildAmsterdamPayload(MergeTestBlockchain chain)
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
            SlotNumber = head.SlotNumber + 1,
            TargetGasLimit = head.GasLimit
        };
        ForkchoiceStateV1 state = new(head.Hash!, head.Hash!, head.Hash!);
        string payloadId = (await rpc.engine_forkchoiceUpdatedV4(state, attributes)).Data.PayloadId!;
        GetPayloadV6Result result = (await rpc.engine_getPayloadV6(Bytes.FromHexString(payloadId))).Data!;
        result.ExecutionPayload.ExecutionRequests = result.ExecutionRequests;
        return (result.ExecutionPayload, result.ExecutionRequests ?? []);
    }
}
