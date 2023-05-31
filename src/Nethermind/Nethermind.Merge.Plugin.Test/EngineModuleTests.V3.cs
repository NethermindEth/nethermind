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
using Nethermind.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.GC;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Specs.Forks;
using NSubstitute;
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

    [Test]
    public async Task NewPayloadV3_should_decline_null_args()
    {
        MergeTestBlockchain chain = await CreateBlockChain(releaseSpec: Cancun.Instance);
        IEngineRpcModule rpcModule = CreateEngineModule(chain);
        ExecutionPayload executionPayload = CreateBlockRequest(
            CreateParentBlockRequestOnHead(chain.BlockTree), TestItem.AddressD, withdrawals: Array.Empty<Withdrawal>());

        int errorCode = (await rpcModule.engine_newPayloadV3(executionPayload, null!)).ErrorCode;

        Assert.That(errorCode, Is.EqualTo(ErrorCodes.InvalidParams));

        errorCode = (await rpcModule.engine_newPayloadV3(null!, Array.Empty<byte[]>())).ErrorCode;

        Assert.That(errorCode, Is.EqualTo(ErrorCodes.InvalidParams));
    }

    private const string FurtherValidationStatus = "FurtherValidation";

    [TestCaseSource(nameof(BlobVersionedHashesMatchTestSource))]
    [TestCaseSource(nameof(BlobVersionedHashesDoNotMatchTestSource))]
    public async Task<string> NewPayloadV3_should_verify_blob_versioned_hashes_against_transactions_ones
        (byte[] hashesFirstBytes, byte[][] transactionsAndFirstBytesOfTheirHashes)
    {
        MergeTestBlockchain chain = await CreateBlockChain(releaseSpec: Cancun.Instance);
        IAsyncHandler<ExecutionPayload, PayloadStatusV1> newPayloadHandlerMock =
            Substitute.For<IAsyncHandler<ExecutionPayload, PayloadStatusV1>>();
        newPayloadHandlerMock.HandleAsync(Arg.Any<ExecutionPayload>())
            .Returns(Task.FromResult(ResultWrapper<PayloadStatusV1>
                                     .Success(new PayloadStatusV1() { Status = FurtherValidationStatus })));

        IEngineRpcModule rpcModule = new EngineRpcModule(
             Substitute.For<IAsyncHandler<byte[], ExecutionPayload?>>(),
             Substitute.For<IAsyncHandler<byte[], GetPayloadV2Result?>>(),
             Substitute.For<IAsyncHandler<byte[], GetPayloadV3Result?>>(),
             newPayloadHandlerMock,
             Substitute.For<IForkchoiceUpdatedHandler>(),
             Substitute.For<IAsyncHandler<IList<Keccak>, IEnumerable<ExecutionPayloadBodyV1Result?>>>(),
             Substitute.For<IGetPayloadBodiesByRangeV1Handler>(),
             Substitute.For<IHandler<TransitionConfigurationV1, TransitionConfigurationV1>>(),
             Substitute.For<IHandler<IEnumerable<string>, IEnumerable<string>>>(),
             chain.SpecProvider,
             new GCKeeper(NoGCStrategy.Instance, chain.LogManager),
             Substitute.For<ILogManager>());


        byte[][] blobVersionedHashes = new byte[hashesFirstBytes.Length][];


        ulong index = 0;
        foreach (byte hashByte in hashesFirstBytes)
        {
            blobVersionedHashes[index] = new byte[32];
            blobVersionedHashes[index][0] = KzgPolynomialCommitments.KzgBlobHashVersionV1;
            blobVersionedHashes[index][1] = hashByte;
            index++;
        }

        ulong txIndex = 0;
        Transaction[] transactions = new Transaction[transactionsAndFirstBytesOfTheirHashes.Length];

        foreach (byte[] txHashBytes in transactionsAndFirstBytesOfTheirHashes)
        {
            ulong txHashIndex = 0;
            byte[][] txBlobVersionedHashes = new byte[txHashBytes.Length][];
            foreach (byte hashByte in txHashBytes)
            {
                txBlobVersionedHashes[txHashIndex] = new byte[32];
                txBlobVersionedHashes[txHashIndex][0] = KzgPolynomialCommitments.KzgBlobHashVersionV1;
                txBlobVersionedHashes[txHashIndex][1] = hashByte;
                txHashIndex++;
            }
            transactions[txIndex] = Build.A.Transaction.WithNonce((ulong)txIndex)
                .WithType(TxType.Blob)
                .WithTimestamp(Timestamper.UnixTime.Seconds)
                .WithTo(TestItem.AddressB)
                .WithValue(1.GWei())
                .WithGasPrice(1.GWei())
                .WithMaxFeePerDataGas(1.GWei())
                .WithChainId(chain.SpecProvider.ChainId)
                .WithSenderAddress(TestItem.AddressA)
                .WithBlobVersionedHashes(txBlobVersionedHashes)
                .WithMaxFeePerGasIfSupports1559(1.GWei())
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            txIndex++;
        }

        ExecutionPayload executionPayload = CreateBlockRequest(
            CreateParentBlockRequestOnHead(chain.BlockTree), TestItem.AddressD, withdrawals: Array.Empty<Withdrawal>(), transactions: transactions);
        var result = await rpcModule.engine_newPayloadV3(executionPayload, blobVersionedHashes);
        return result.Data.Status;
    }

    public static IEnumerable<TestCaseData> BlobVersionedHashesMatchTestSource
    {
        get
        {
            yield return new TestCaseData(new byte[] { }, new byte[][] { })
            {
                ExpectedResult = FurtherValidationStatus,
                TestName = "Zero hashes passed, as expected",
            };
            yield return new TestCaseData(new byte[] { 0, 1 }, new byte[][] { new byte[] { 0, 1 } })
            {
                ExpectedResult = FurtherValidationStatus,
                TestName = "N hashes passed, as expected",
            };
            yield return new TestCaseData(new byte[] { 0, 1 }, new byte[][] { new byte[] { 0 }, new byte[] { 1 } })
            {
                ExpectedResult = FurtherValidationStatus,
                TestName = "N hashes passed, as expected, multiple transactions",
            };
        }
    }

    public static IEnumerable<TestCaseData> BlobVersionedHashesDoNotMatchTestSource
    {
        get
        {
            yield return new TestCaseData(new byte[] { }, new byte[][] { new byte[] { 0 } })
            {
                ExpectedResult = PayloadStatus.Invalid,
                TestName = "Zero hashes passed, but a tx has one",
            };
            yield return new TestCaseData(new byte[] { 0, 1, 2 }, new byte[][] { new byte[] { 0, 2, 1 } })
            {
                ExpectedResult = PayloadStatus.Invalid,
                TestName = "Order is not correct",
            };
            yield return new TestCaseData(new byte[] { 0, 1, 2 }, new byte[][] { new byte[] { 2 }, new byte[] { 0, 1 } })
            {
                ExpectedResult = PayloadStatus.Invalid,
                TestName = "Order is not correct, multiple transactions",
            };
            yield return new TestCaseData(new byte[] { 0, 2 }, new byte[][] { new byte[] { 0, 1, 2 } })
            {
                ExpectedResult = PayloadStatus.Invalid,
                TestName = "A hash is missing",
            };
            yield return new TestCaseData(new byte[] { 0, 1, 2 }, new byte[][] { new byte[] { 0, 1 } })
            {
                ExpectedResult = PayloadStatus.Invalid,
                TestName = "One hash more than expected",
            };
        }
    }

    private async Task<ExecutionPayload> SendNewBlockV3(IEngineRpcModule rpc, MergeTestBlockchain chain, IList<Withdrawal>? withdrawals)
    {
        ExecutionPayload executionPayload = CreateBlockRequest(
            CreateParentBlockRequestOnHead(chain.BlockTree), TestItem.AddressD, withdrawals, 0);
        ResultWrapper<PayloadStatusV1> executePayloadResult = await rpc.engine_newPayloadV3(executionPayload, Array.Empty<byte[]>());

        executePayloadResult.Data.Status.Should().Be(PayloadStatus.Valid);

        return executionPayload;
    }

    private async Task<(IEngineRpcModule, string)> BuildAndGetPayloadV3Result(
        IReleaseSpec spec, int transactionCount = 0)
    {
        MergeTestBlockchain chain = await CreateBlockChain(releaseSpec: spec);
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
