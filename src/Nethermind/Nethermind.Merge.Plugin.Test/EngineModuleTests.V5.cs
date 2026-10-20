// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using CkzgLib;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.TxPool;
using NUnit.Framework;
using NSubstitute;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [TestCaseSource(nameof(GetPayloadForkCases))]
    public async Task GetPayload_fork_windows_are_preserved(IReleaseSpec spec, int version, bool supported)
    {
        Block block = Build.A.Block.TestObject;
        block.ExecutionRequests = [];
        IPayloadPreparationService payloadService = Substitute.For<IPayloadPreparationService>();
        payloadService.GetPayload(Arg.Any<string>()).Returns(new BlockProductionSnapshot(block, UInt256.Zero));
        using MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: spec, mockedPayloadService: payloadService);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        byte[] id = new byte[8];

        int errorCode = version switch
        {
            1 => (await rpc.engine_getPayloadV1(id)).ErrorCode,
            2 => (await rpc.engine_getPayloadV2(id)).ErrorCode,
            3 => (await rpc.engine_getPayloadV3(id)).ErrorCode,
            4 => (await rpc.engine_getPayloadV4(id)).ErrorCode,
            5 => (await rpc.engine_getPayloadV5(id)).ErrorCode,
            _ => (await rpc.engine_getPayloadV6(id)).ErrorCode
        };

        Assert.That(errorCode, Is.EqualTo(supported ? 0 : MergeErrorCodes.UnsupportedFork));
    }

    private static IEnumerable<TestCaseData> GetPayloadForkCases()
    {
        (IReleaseSpec Spec, int[] Versions)[] forks =
        [
            (Shanghai.Instance, [1, 2]),
            (Cancun.Instance, [3]),
            (Prague.Instance, [4]),
            (Osaka.Instance, [5]),
            (Amsterdam.Instance, [6])
        ];
        foreach ((IReleaseSpec spec, int[] versions) in forks)
        {
            for (int version = 1; version <= 6; version++)
            {
                yield return new TestCaseData(spec, version, versions.Contains(version));
            }
        }
    }

    [Test]
    public async Task GetPayload_should_reject_wrong_fork_before_building_cell_proofs([Values(5, 6)] int version)
    {
        CustomSpecProvider specProvider = new(
            (new(0), Prague.Instance),
            (new(0, 10_000), Osaka.Instance),
            (new(0, 20_000), Amsterdam.Instance));
        (IEngineRpcModule rpc, string? payloadId, _, MergeTestBlockchain chain) =
            await BuildAndGetPayloadV3Result(Prague.Instance, transactionCount: 1, specProvider: specProvider);
        using MergeTestBlockchain disposeChain = chain;

        ResultWrapper<GetPayloadV4Result?> pragueResult = await rpc.engine_getPayloadV4(Bytes.FromHexString(payloadId!));
        Assert.That(pragueResult.Result, Is.EqualTo(Result.Success));
        Assert.That(pragueResult.Data, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pragueResult.Data.ExecutionPayload.Transactions, Has.Length.EqualTo(1));
            Assert.That(pragueResult.Data.BlobsBundle.Proofs, Has.Length.EqualTo(1));
        }

        string json = await RpcTest.TestSerializedRequest(rpc, $"engine_getPayloadV{version}", payloadId);
        using JsonDocument response = JsonDocument.Parse(json);
        JsonElement error = response.RootElement.GetProperty("error");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(error.GetProperty("code").GetInt32(), Is.EqualTo(MergeErrorCodes.UnsupportedFork));
            Assert.That(error.GetProperty("message").GetString(), Is.EqualTo(MergeErrorMessages.UnsupportedFork));
            Assert.That(response.RootElement.TryGetProperty("result", out _), Is.False);
        }
    }

    [Test]
    public async Task GetPayload_should_fail_for_same_fork_blob_without_cell_proofs([Values(5, 6)] int version)
    {
        Transaction transaction = Build.A.Transaction.WithShardBlobTxTypeAndFields(1, isMempoolTx: false).TestObject;
        Block block = Build.A.Block.WithTransactions(transaction).TestObject;
        IPayloadPreparationService payloadService = Substitute.For<IPayloadPreparationService>();
        payloadService.GetPayload(Arg.Any<string>()).Returns(new BlockProductionSnapshot(block, UInt256.Zero));
        using MergeTestBlockchain chain = await CreateBlockchain(
            releaseSpec: version == 5 ? Osaka.Instance : Amsterdam.Instance, mockedPayloadService: payloadService);

        string json = await RpcTest.TestSerializedRequest(chain.EngineRpcModule, $"engine_getPayloadV{version}", "0x0000000000000000");
        using JsonDocument response = JsonDocument.Parse(json);
        JsonElement error = response.RootElement.GetProperty("error");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(error.GetProperty("code").GetInt32(), Is.EqualTo(ErrorCodes.InvalidParams));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("Shard blob transaction should contain network wrapper data"));
            Assert.That(response.RootElement.TryGetProperty("result", out _), Is.False);
        }
    }

    [Test]
    public async Task GetPayload_should_return_unknown_payload_before_checking_fork([Values(5, 6)] int version)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Prague.Instance);
        string json = await RpcTest.TestSerializedRequest(chain.EngineRpcModule, $"engine_getPayloadV{version}", "0x0000000000000000");
        using JsonDocument response = JsonDocument.Parse(json);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.RootElement.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(MergeErrorCodes.UnknownPayload));
            Assert.That(response.RootElement.TryGetProperty("result", out _), Is.False);
        }
    }

    [Test]
    public async Task GetPayloadV5_should_return_unsupported_fork_at_amsterdam()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Amsterdam.Instance);
        IEngineRpcModule rpcModule = chain.EngineRpcModule;
        Block head = chain.BlockTree.Head!;
        PayloadAttributes payloadAttributes = CreateAmsterdamPayloadAttributes(head.Header);
        ForkchoiceStateV1 forkchoiceState = new(head.Hash!, head.Hash!, head.Hash!);

        ResultWrapper<ForkchoiceUpdatedV1Result> fcuResponse =
            await rpcModule.engine_forkchoiceUpdatedV4(forkchoiceState, payloadAttributes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fcuResponse.Result, Is.EqualTo(Result.Success));
            Assert.That(fcuResponse.Data.PayloadId, Is.Not.Null);
        }

        ResultWrapper<GetPayloadV5Result?> result =
            await rpcModule.engine_getPayloadV5(Bytes.FromHexString(fcuResponse.Data.PayloadId!));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result, Is.EqualTo(Result.Fail(MergeErrorMessages.UnsupportedFork)));
            Assert.That(result.ErrorCode, Is.EqualTo(MergeErrorCodes.UnsupportedFork));
        }
    }

    [Test]
    public async Task GetPayloadV5_should_return_all_the_blobs([Values(0, 1, 2, 3, 4)] int blobTxCount, [Values(true, false)] bool oneBlobPerTx)
    {
        (IEngineRpcModule rpcModule, string? payloadId, _, MergeTestBlockchain chain) = await BuildAndGetPayloadV3Result(Osaka.Instance, blobTxCount, oneBlobPerTx: oneBlobPerTx);
        using MergeTestBlockchain disposeChain = chain;
        ResultWrapper<GetPayloadV5Result?> result = await rpcModule.engine_getPayloadV5(Bytes.FromHexString(payloadId!));
        BlobsBundleV2 getPayloadResultBlobsBundle = result.Data!.BlobsBundle!;
        ShardBlobNetworkWrapper wrapper = new(getPayloadResultBlobsBundle.Blobs,
            getPayloadResultBlobsBundle.Commitments, getPayloadResultBlobsBundle.Proofs, ProofVersion.V1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Data.ExecutionPayload.BlobGasUsed, Is.EqualTo(BlobGasCalculator.CalculateBlobGas(blobTxCount)));
            Assert.That(getPayloadResultBlobsBundle.Blobs!.Length, Is.EqualTo(blobTxCount));
            Assert.That(getPayloadResultBlobsBundle.Commitments!.Length, Is.EqualTo(blobTxCount));
            Assert.That(getPayloadResultBlobsBundle.Proofs!.Length, Is.EqualTo(blobTxCount * Ckzg.CellsPerExtBlob));
            Assert.That(IBlobProofsManager.For(ProofVersion.V1).ValidateProofs(wrapper), Is.True);
        }
    }

    [Test]
    public async Task Testing_buildBlockV1_empty_block_with_empty_withdrawals_has_valid_hash()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Osaka.Instance);
        ITestingRpcModule testingRpcModule = chain.Container.Resolve<ITestingRpcModule>();

        Block head = chain.BlockTree.Head!;
        PayloadAttributes payloadAttributes = new()
        {
            Timestamp = head.Timestamp + 12,
            PrevRandao = TestItem.KeccakA,
            SuggestedFeeRecipient = Address.Zero,
            Withdrawals = [],
            ParentBeaconBlockRoot = TestItem.KeccakB
        };

        ResultWrapper<object> buildResult = await testingRpcModule.testing_buildBlockV1(
            head.Hash!,
            payloadAttributes,
            [],
            []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(buildResult.Result, Is.EqualTo(Result.Success));
            Assert.That(buildResult.Data, Is.Not.Null);
            Assert.That(buildResult.Data, Is.AssignableTo<GetPayloadV5Result>());
        }
        GetPayloadV5Result payloadResult = (GetPayloadV5Result)buildResult.Data!;

        ExecutionPayloadV3 executionPayload = payloadResult.ExecutionPayload;
        executionPayload.ExecutionRequests = payloadResult.ExecutionRequests;
        Assert.That(executionPayload.TryGetBlock().Data!.CalculateHash(), Is.EqualTo(executionPayload.BlockHash));

        ResultWrapper<PayloadStatusV1> newPayloadResult = await chain.EngineRpcModule.engine_newPayloadV4(
            executionPayload,
            [],
            payloadAttributes.ParentBeaconBlockRoot,
            payloadResult.ExecutionRequests);

        Assert.That(newPayloadResult.Result, Is.EqualTo(Result.Success));
        Assert.That(newPayloadResult.Data.Status, Is.EqualTo(PayloadStatus.Valid));
    }

    [Test]
    public async Task Testing_commitBlockV1_advances_chain_head()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Osaka.Instance);
        ITestingRpcModule testingRpcModule = chain.Container.Resolve<ITestingRpcModule>();

        Block head = chain.BlockTree.Head!;
        ulong initialHeadNumber = head.Number;

        PayloadAttributes payloadAttributes = new()
        {
            Timestamp = head.Timestamp + 12,
            PrevRandao = TestItem.KeccakA,
            SuggestedFeeRecipient = Address.Zero,
            Withdrawals = [],
            ParentBeaconBlockRoot = TestItem.KeccakB
        };

        ResultWrapper<Hash256> result = await testingRpcModule.testing_commitBlockV1(payloadAttributes, [], []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result, Is.EqualTo(Result.Success));
            Assert.That(result.Data, Is.Not.Null);
            Assert.That(chain.BlockTree.Head!.Number, Is.EqualTo(initialHeadNumber + 1));
            Assert.That(chain.BlockTree.Head.Hash, Is.EqualTo(result.Data));
        }
    }

    [Test]
    public async Task GetBlobsV2_should_throw_if_more_than_128_requested_blobs([Values(128, 129)] int requestSize)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Osaka.Instance);
        IEngineRpcModule rpcModule = chain.EngineRpcModule;

        List<byte[]> request = new(requestSize);
        for (int i = 0; i < requestSize; i++)
        {
            request.Add(Bytes.FromHexString(i.ToString("X64")));
        }

        ResultWrapper<IReadOnlyList<BlobAndProofV2?>?> result = await rpcModule.engine_getBlobsV2(request.ToArray());

        if (requestSize > 128)
        {
            Assert.That(result.Result, Is.EqualTo(Result.Fail($"The number of requested blobs must not exceed 128")));
            Assert.That(result.ErrorCode, Is.EqualTo(MergeErrorCodes.TooLargeRequest));
        }
        else
        {
            Assert.That(result.Result, Is.EqualTo(Result.Success));
            Assert.That(result.Data, Is.Null);
        }
    }

    [Test]
    public async Task GetBlobs_should_handle_empty_request([Values(2, 3)] int version)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Osaka.Instance);
        IEngineRpcModule rpcModule = chain.EngineRpcModule;

        string response = await RpcTest.TestSerializedRequest(rpcModule, $"engine_getBlobsV{version}", (object)Array.Empty<byte[]>());

        Assert.That(response, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":[],\"id\":67}"));
    }

    [Test]
    public async Task GetBlobsV2_should_return_requested_blobs([Values(1, 2, 3, 4, 5, 6)] int numberOfBlobs)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Osaka.Instance);
        IEngineRpcModule rpcModule = chain.EngineRpcModule;

        Transaction blobTx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(numberOfBlobs, spec: Osaka.Instance)
            .WithMaxFeePerGas(1.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .WithMaxFeePerBlobGas(1000.Wei)
            .SignedAndResolved(chain.EthereumEcdsa, TestItem.PrivateKeyA).TestObject;

        Assert.That(chain.TxPool.SubmitTx(blobTx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

        ResultWrapper<IReadOnlyList<BlobAndProofV2?>?> result = await rpcModule.engine_getBlobsV2(blobTx.BlobVersionedHashes!);

        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)blobTx.NetworkWrapper!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Data, Is.Not.Null);
            Assert.That(result.Data!.Select(static b => b!.Blob), Is.EqualTo(wrapper.Blobs));
            Assert.That(result.Data, Has.Count.EqualTo(numberOfBlobs));
            Assert.That(result.Data!.Select(static b => b!.Proofs), Is.EqualTo(wrapper.Proofs.Chunk(128)));
        }
    }

    [Test]
    public async Task GetBlobsV2_should_return_blobs_of_a_transaction_included_in_the_head([Values(1, 3)] int numberOfBlobs)
    {
        (IEngineRpcModule rpcModule, string? payloadId, Transaction[] txs, MergeTestBlockchain chain) =
            await BuildAndGetPayloadV3Result(Osaka.Instance, numberOfBlobs, oneBlobPerTx: false);
        using MergeTestBlockchain disposeChain = chain;
        Transaction blobTx = txs.Single();
        byte[][] blobVersionedHashes = blobTx.BlobVersionedHashes!.Select(static h => h!).ToArray();
        GetPayloadV5Result payload = (await rpcModule.engine_getPayloadV5(Bytes.FromHexString(payloadId!))).Data!;
        ExecutionPayloadV3 executionPayload = payload.ExecutionPayload;

        ResultWrapper<PayloadStatusV1> newPayloadResult = await rpcModule.engine_newPayloadV4(
            executionPayload, blobVersionedHashes.Select(static h => new Hash256(h)).ToArray(), executionPayload.ParentBeaconBlockRoot, payload.ExecutionRequests);
        Assert.That(newPayloadResult.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        // Not finalized: the blobs of finalized blocks are no longer served.
        await rpcModule.engine_forkchoiceUpdatedV3(new ForkchoiceStateV1(executionPayload.BlockHash, Keccak.Zero, Keccak.Zero), null);

        Assert.That(() => chain.TxPool.GetPendingBlobTransactionsCount(), Is.Zero.After(5000, 10), "precondition: the transaction left the blob pool");
        ResultWrapper<IReadOnlyList<BlobAndProofV2?>?> result = await rpcModule.engine_getBlobsV2(blobVersionedHashes);

        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)blobTx.NetworkWrapper!;
        Assert.That(result.Data, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.BlockTree.Head!.Hash, Is.EqualTo(executionPayload.BlockHash));
            Assert.That(result.Data!.Select(static b => b!.Blob), Is.EqualTo(wrapper.Blobs));
            Assert.That(result.Data!.Select(static b => b!.Proofs), Is.EqualTo(wrapper.Proofs.Chunk(Ckzg.CellsPerExtBlob)));
        }
    }

    [Test]
    public async Task GetBlobsV2_should_return_empty_array_when_blobs_not_found([Values(1, 2, 3, 4, 5, 6)] int numberOfRequestedBlobs)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Osaka.Instance);
        IEngineRpcModule rpcModule = chain.EngineRpcModule;

        // we are not adding this tx
        Transaction blobTx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(numberOfRequestedBlobs)
            .WithMaxFeePerGas(1.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .WithMaxFeePerBlobGas(1000.Wei)
            .SignedAndResolved(chain.EthereumEcdsa, TestItem.PrivateKeyA).TestObject;

        // requesting hashes that are not present in TxPool
        ResultWrapper<IReadOnlyList<BlobAndProofV2?>?> result = await rpcModule.engine_getBlobsV2(blobTx.BlobVersionedHashes!);

        Assert.That(result.Result, Is.EqualTo(Result.Success));
        Assert.That(result.Data, Is.Null);
    }

    [Test]
    public async Task GetBlobsV2_should_return_empty_array_when_only_some_blobs_found([Values(1, 2, 3, 4, 5, 6)] int numberOfBlobs, [Values(1, 2)] int multiplier)
    {
        int requestSize = multiplier * numberOfBlobs;

        using MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Osaka.Instance);
        IEngineRpcModule rpcModule = chain.EngineRpcModule;

        Transaction blobTx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(numberOfBlobs, spec: Osaka.Instance)
            .WithMaxFeePerGas(1.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .WithMaxFeePerBlobGas(1000.Wei)
            .SignedAndResolved(chain.EthereumEcdsa, TestItem.PrivateKeyA).TestObject;

        Assert.That(chain.TxPool.SubmitTx(blobTx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

        List<byte[]> blobVersionedHashesRequest = new(requestSize);

        int actualIndex = 0;
        for (int i = 0; i < requestSize; i++)
        {
            bool addActualHash = i % multiplier == 0;
            blobVersionedHashesRequest.Add(addActualHash ? blobTx.BlobVersionedHashes![actualIndex++]! : Bytes.FromHexString(i.ToString("X64")));
        }

        ResultWrapper<IReadOnlyList<BlobAndProofV2?>?> result = await rpcModule.engine_getBlobsV2(blobVersionedHashesRequest.ToArray());
        if (multiplier > 1)
        {
            Assert.That(result.Result, Is.EqualTo(Result.Success));
            Assert.That(result.Data, Is.Null);
        }
        else
        {
            ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)blobTx.NetworkWrapper!;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Data, Is.Not.Null);
                Assert.That(result.Data!.Select(static b => b!.Blob), Is.EqualTo(wrapper.Blobs));
                Assert.That(result.Data, Has.Count.EqualTo(numberOfBlobs));
                Assert.That(result.Data!.Select(static b => b!.Proofs), Is.EqualTo(wrapper.Proofs.Chunk(128)));
            }
        }
    }

    [Test]
    public async Task GetBlobsV3_should_return_partial_results_with_nulls_for_missing_blobs([Values(1, 2, 3, 4, 5, 6)] int numberOfBlobs, [Values(1, 2)] int multiplier)
    {
        int requestSize = multiplier * numberOfBlobs;

        using MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Osaka.Instance);
        IEngineRpcModule rpcModule = chain.EngineRpcModule;

        Transaction blobTx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(numberOfBlobs, spec: Osaka.Instance)
            .WithMaxFeePerGas(1.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .WithMaxFeePerBlobGas(1000.Wei)
            .SignedAndResolved(chain.EthereumEcdsa, TestItem.PrivateKeyA).TestObject;

        Assert.That(chain.TxPool.SubmitTx(blobTx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

        List<byte[]> blobVersionedHashesRequest = new(requestSize);

        int actualIndex = 0;
        for (int i = 0; i < requestSize; i++)
        {
            bool addActualHash = i % multiplier == 0;
            blobVersionedHashesRequest.Add(addActualHash ? blobTx.BlobVersionedHashes![actualIndex++]! : Bytes.FromHexString(i.ToString("X64")));
        }

        ResultWrapper<IReadOnlyList<BlobAndProofV2?>?> result = await rpcModule.engine_getBlobsV3(blobVersionedHashesRequest.ToArray());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result, Is.EqualTo(Result.Success));
            Assert.That(result.Data, Is.Not.Null);
            Assert.That(result.Data!, Has.Count.EqualTo(requestSize));
        }

        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)blobTx.NetworkWrapper!;

        // V3 returns partial results with nulls for missing blobs
        int foundIndex = 0;
        for (int i = 0; i < requestSize; i++)
        {
            bool shouldBeFound = i % multiplier == 0;
            if (shouldBeFound)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(result.Data!.ElementAt(i), Is.Not.Null);
                    Assert.That(result.Data!.ElementAt(i)!.Blob, Is.EqualTo(wrapper.Blobs[foundIndex]));
                    Assert.That(result.Data!.ElementAt(i)!.Proofs, Is.EqualTo(wrapper.Proofs.Skip(foundIndex * 128).Take(128)));
                }
                foundIndex++;
            }
            else
            {
                Assert.That(result.Data!.ElementAt(i), Is.Null);
            }
        }
    }

    [Test]
    public async Task GetBlobsV1_should_return_invalid_fork_post_osaka()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Osaka.Instance);
        IEngineRpcModule rpcModule = chain.EngineRpcModule;

        ResultWrapper<IReadOnlyList<BlobAndProofV1?>> result = await rpcModule.engine_getBlobsV1([]);

        Assert.That(result.Result, Is.EqualTo(Result.Fail(MergeErrorMessages.UnsupportedFork)));
        Assert.That(result.ErrorCode, Is.EqualTo(MergeErrorCodes.UnsupportedFork));
    }

    [Test]
    public async Task BlobsV2DirectResponse_WriteToAsync_produces_valid_json()
    {
        byte[]?[] blobs = [RandomBytes(16), null];
        ReadOnlyMemory<byte[]>[] proofs = [new ReadOnlyMemory<byte[]>([RandomBytes(48), RandomBytes(48)]), default];

        BlobsV2DirectResponse response = new(blobs, proofs, 2);
        await AssertStreamedJsonMatchesSerializer(response);
    }

    [Test]
    public async Task BlobsV2DirectResponse_WriteToAsync_empty_list()
    {
        BlobsV2DirectResponse response = new([], [], 0);

        string json = await AssertStreamedJsonMatchesSerializer(response);
        Assert.That(json, Is.EqualTo("[]"));
    }

    [Test]
    public void BlobsV2DirectResponse_WriteToAsync_throws_on_cancelled_token()
    {
        byte[] blob = new byte[131072]; // 128KB
        byte[]?[] blobs = [blob];
        ReadOnlyMemory<byte[]>[] proofs = [new ReadOnlyMemory<byte[]>([new byte[48]])];
        BlobsV2DirectResponse response = new(blobs, proofs, 1);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        Pipe pipe = new();
        Func<Task> act = async () => await response.WriteToAsync(pipe.Writer, cts.Token);
        Assert.That(async () => await act(), Throws.TypeOf<OperationCanceledException>());
    }
}
