// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Specs.Forks;
using NUnit.Framework;
using Transaction = Nethermind.Core.Transaction;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [TestCaseSource(nameof(ExcessDataGasInGetPayloadV3ForDifferentSpecTestSource))]
    public async Task ExccessDataGas_should_present_in_cancun_only((IReleaseSpec Spec, bool IsExcessDataGasSet) input)
    {
        (IEngineRpcModule rpcModule, string payloadId) = await BuildAndGetPayloadV3Result(input.Spec);
        ResultWrapper<GetPayloadV3Result?> getPayloadResult =
            await rpcModule.engine_getPayloadV3(Bytes.FromHexString(payloadId));
        Assert.That(getPayloadResult.Data!.ExecutionPayload.ExcessDataGas.HasValue,
            Is.EqualTo(input.IsExcessDataGasSet));
    }

    [Test]
    public async Task GetPayloadV3_should_fail_on_unknown_payload()
    {
        using SemaphoreSlim blockImprovementLock = new(0);
        using MergeTestBlockchain chain = await CreateBlockChain();
        IEngineRpcModule rpc = CreateEngineModule(chain);

        byte[] payloadId = Bytes.FromHexString("0x0");
        ResultWrapper<GetPayloadV3Result?> responseFirst = await rpc.engine_getPayloadV3(payloadId);
        responseFirst.Should().NotBeNull();
        responseFirst.Result.ResultType.Should().Be(ResultType.Failure);
        responseFirst.ErrorCode.Should().Be(MergeErrorCodes.UnknownPayload);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    public async Task PayloadV3_should_return_all_the_blobs(int blobTxCount)
    {
        (IEngineRpcModule rpcModule, string payloadId) = await BuildAndGetPayloadV3Result(Cancun.Instance, blobTxCount);
        BlobsBundleV1 getPayloadResultBlobsBundle =
            (await rpcModule.engine_getPayloadV3(Bytes.FromHexString(payloadId))).Data!.BlobsBundle!;
        Assert.That(getPayloadResultBlobsBundle.Blobs!.Length, Is.EqualTo(blobTxCount));
        Assert.That(getPayloadResultBlobsBundle.Commitments!.Length, Is.EqualTo(blobTxCount));
        Assert.That(getPayloadResultBlobsBundle.Proofs!.Length, Is.EqualTo(blobTxCount));
    }

    private async Task<ExecutionPayload> SendNewBlockV3(IEngineRpcModule rpc, MergeTestBlockchain chain, IList<Withdrawal>? withdrawals)
    {
        ExecutionPayload executionPayload = CreateBlockRequest(
            CreateParentBlockRequestOnHead(chain.BlockTree), TestItem.AddressD, withdrawals, 0);
        ResultWrapper<PayloadStatusV1> executePayloadResult = await rpc.engine_newPayloadV3(executionPayload);

        executePayloadResult.Data.Status.Should().Be(PayloadStatus.Valid);

        return executionPayload;
    }

    private async Task<(IEngineRpcModule, string)> BuildAndGetPayloadV3Result(
        IReleaseSpec spec, int transactionCount = 0)
    {
        MergeTestBlockchain chain = await CreateBlockChain(releaseSpec: spec, null);
        IEngineRpcModule rpcModule = CreateEngineModule(chain);
        if (transactionCount is not 0)
        {
            using SemaphoreSlim blockImprovementLock = new(0);

            ExecutionPayload executionPayload1 = await SendNewBlockV3(rpcModule, chain, new List<Withdrawal>());
            Transaction[] txs = BuildTransactions(
                chain, executionPayload1.BlockHash, TestItem.PrivateKeyA, TestItem.AddressB, (uint)transactionCount, 0, out _, out _, 1);
            chain.AddTransactions(txs);

            EventHandler<BlockEventArgs> onBlockImprovedHandler = (_, _) => blockImprovementLock.Release(1);

            chain.PayloadPreparationService!.BlockImproved += onBlockImprovedHandler;
            await blockImprovementLock.WaitAsync(10000);
            chain.PayloadPreparationService!.BlockImproved -= onBlockImprovedHandler;
        }

        PayloadAttributes payloadAttributes = new()
        {
            Timestamp = chain.BlockTree.Head!.Timestamp + 1,
            PrevRandao = TestItem.KeccakH,
            SuggestedFeeRecipient = TestItem.AddressF,
            Withdrawals = new List<Withdrawal> { TestItem.WithdrawalA_1Eth }
        };
        Keccak currentHeadHash = chain.BlockTree.HeadHash;
        ForkchoiceStateV1 forkchoiceState = new(currentHeadHash, currentHeadHash, currentHeadHash);
        string payloadId = rpcModule.engine_forkchoiceUpdatedV2(forkchoiceState, payloadAttributes).Result.Data
            .PayloadId!;
        return (rpcModule, payloadId);
    }

    protected static IEnumerable<(IReleaseSpec Spec, bool IsExcessDataGasSet)> ExcessDataGasInGetPayloadV3ForDifferentSpecTestSource()
    {
        yield return (Shanghai.Instance, false);
        yield return (Cancun.Instance, true);
    }
}
