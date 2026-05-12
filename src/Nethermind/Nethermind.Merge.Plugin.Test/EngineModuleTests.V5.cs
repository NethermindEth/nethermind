// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using CkzgLib;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Json;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [Test]
    public async Task GetPayloadV5_should_return_all_the_blobs([Values(0, 1, 2, 3, 4)] int blobTxCount, [Values(true, false)] bool oneBlobPerTx)
    {
        (IEngineRpcModule rpcModule, string? payloadId, _, _) = await BuildAndGetPayloadV3Result(Osaka.Instance, blobTxCount, oneBlobPerTx: oneBlobPerTx);
        ResultWrapper<GetPayloadV5Result?> result = await rpcModule.engine_getPayloadV5(Bytes.FromHexString(payloadId!));
        BlobsBundleV2 getPayloadResultBlobsBundle = result.Data!.BlobsBundle!;
        Assert.That(result.Data.ExecutionPayload.BlobGasUsed, Is.EqualTo(BlobGasCalculator.CalculateBlobGas(blobTxCount)));
        Assert.That(getPayloadResultBlobsBundle.Blobs!.Length, Is.EqualTo(blobTxCount));
        Assert.That(getPayloadResultBlobsBundle.Commitments!.Length, Is.EqualTo(blobTxCount));
        Assert.That(getPayloadResultBlobsBundle.Proofs!.Length, Is.EqualTo(blobTxCount * Ckzg.CellsPerExtBlob));
        ShardBlobNetworkWrapper wrapper = new(getPayloadResultBlobsBundle.Blobs,
            getPayloadResultBlobsBundle.Commitments, getPayloadResultBlobsBundle.Proofs, ProofVersion.V1);
        Assert.That(IBlobProofsManager.For(ProofVersion.V1).ValidateProofs(wrapper), Is.True);
    }

    [Test]
    public async Task Testing_buildBlockV1_empty_block_with_empty_withdrawals_has_valid_hash()
    {
        MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Osaka.Instance);
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

        Assert.That(buildResult.Result, Is.EqualTo(Result.Success));
        Assert.That(buildResult.Data, Is.Not.Null);
        Assert.That(buildResult.Data, Is.TypeOf<GetPayloadV5Result>());
        GetPayloadV5Result payloadResult = (GetPayloadV5Result)buildResult.Data!;

        ExecutionPayloadV3 executionPayload = payloadResult.ExecutionPayload;
        executionPayload.ExecutionRequests = payloadResult.ExecutionRequests;
        Assert.That(executionPayload.TryGetBlock().Block!.CalculateHash(), Is.EqualTo(executionPayload.BlockHash));

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
        MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Osaka.Instance);
        ITestingRpcModule testingRpcModule = chain.Container.Resolve<ITestingRpcModule>();

        Block head = chain.BlockTree.Head!;
        long initialHeadNumber = head.Number;

        PayloadAttributes payloadAttributes = new()
        {
            Timestamp = head.Timestamp + 12,
            PrevRandao = TestItem.KeccakA,
            SuggestedFeeRecipient = Address.Zero,
            Withdrawals = [],
            ParentBeaconBlockRoot = TestItem.KeccakB
        };

        ResultWrapper<Hash256> result = await testingRpcModule.testing_commitBlockV1(payloadAttributes, [], []);

        Assert.That(result.Result, Is.EqualTo(Result.Success));
        Assert.That(result.Data, Is.Not.Null);
        Assert.That(chain.BlockTree.Head!.Number, Is.EqualTo(initialHeadNumber + 1));
        Assert.That(chain.BlockTree.Head.Hash, Is.EqualTo(result.Data));
    }

    [Test]
    public async Task GetBlobsV2_should_throw_if_more_than_128_requested_blobs([Values(128, 129)] int requestSize)
    {
        MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Osaka.Instance, mergeConfig: new MergeConfig()
        {
            NewPayloadBlockProcessingTimeout = (int)TimeSpan.FromDays(1).TotalMilliseconds
        });
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
    public async Task GetBlobsV2_should_handle_empty_request()
    {
        MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Osaka.Instance, mergeConfig: new MergeConfig()
        {
            NewPayloadBlockProcessingTimeout = (int)TimeSpan.FromDays(1).TotalMilliseconds
        });
        IEngineRpcModule rpcModule = chain.EngineRpcModule;

        ResultWrapper<IReadOnlyList<BlobAndProofV2?>?> result = await rpcModule.engine_getBlobsV2([]);

        Assert.That(result.Result, Is.EqualTo(Result.Success));
        Assert.That(result.Data, Is.EqualTo(ArraySegment<BlobAndProofV2>.Empty));
    }

    [Test]
    public async Task GetBlobsV2_should_return_requested_blobs([Values(1, 2, 3, 4, 5, 6)] int numberOfBlobs)
    {
        MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Osaka.Instance, mergeConfig: new MergeConfig()
        {
            NewPayloadBlockProcessingTimeout = (int)TimeSpan.FromDays(1).TotalMilliseconds
        });
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

        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data!.Select(static b => b!.Blob), Is.EqualTo(wrapper.Blobs));
        Assert.That(System.Linq.Enumerable.Count(result.Data!.Select(static b => b!.Proofs.Length)), Is.EqualTo(numberOfBlobs));
        Assert.That(result.Data!.Select(static b => b!.Proofs), Is.EqualTo(wrapper.Proofs.Chunk(128)));
    }

    [Test]
    public async Task GetBlobsV2_should_return_empty_array_when_blobs_not_found([Values(1, 2, 3, 4, 5, 6)] int numberOfRequestedBlobs)
    {
        MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Osaka.Instance, mergeConfig: new MergeConfig()
        {
            NewPayloadBlockProcessingTimeout = (int)TimeSpan.FromDays(1).TotalMilliseconds
        });
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

        MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Osaka.Instance, mergeConfig: new MergeConfig()
        {
            NewPayloadBlockProcessingTimeout = (int)TimeSpan.FromDays(1).TotalMilliseconds
        });
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

            Assert.That(result.Data, Is.Not.Null);
            Assert.That(result.Data!.Select(static b => b!.Blob), Is.EqualTo(wrapper.Blobs));
            Assert.That(System.Linq.Enumerable.Count(result.Data!.Select(static b => b!.Proofs.Length)), Is.EqualTo(numberOfBlobs));
            Assert.That(result.Data!.Select(static b => b!.Proofs), Is.EqualTo(wrapper.Proofs.Chunk(128)));
        }
    }

    [Test]
    public async Task GetBlobsV3_should_return_partial_results_with_nulls_for_missing_blobs([Values(1, 2, 3, 4, 5, 6)] int numberOfBlobs, [Values(1, 2)] int multiplier)
    {
        int requestSize = multiplier * numberOfBlobs;

        MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Osaka.Instance, mergeConfig: new MergeConfig()
        {
            NewPayloadBlockProcessingTimeout = (int)TimeSpan.FromDays(1).TotalMilliseconds
        });
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

        Assert.That(result.Result, Is.EqualTo(Result.Success));
        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data!.Count, Is.EqualTo(requestSize));

        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)blobTx.NetworkWrapper!;

        // V3 returns partial results with nulls for missing blobs
        int foundIndex = 0;
        for (int i = 0; i < requestSize; i++)
        {
            bool shouldBeFound = i % multiplier == 0;
            if (shouldBeFound)
            {
                Assert.That(result.Data!.ElementAt(i), Is.Not.Null);
                Assert.That(result.Data!.ElementAt(i)!.Blob, Is.EqualTo(wrapper.Blobs[foundIndex]));
                Assert.That(result.Data!.ElementAt(i)!.Proofs, Is.EqualTo(wrapper.Proofs.Skip(foundIndex * 128).Take(128)));
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
        MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Osaka.Instance);
        IEngineRpcModule rpcModule = chain.EngineRpcModule;

        ResultWrapper<IReadOnlyList<BlobAndProofV1?>> result = await rpcModule.engine_getBlobsV1([]);

        Assert.That(result.Result, Is.EqualTo(Result.Fail(MergeErrorMessages.UnsupportedFork)));
        Assert.That(result.ErrorCode, Is.EqualTo(MergeErrorCodes.UnsupportedFork));
    }

    [Test]
    public async Task BlobsV2DirectResponse_WriteToAsync_produces_valid_json()
    {
        // Build a small list with one real entry and one null
        byte[] blob = new byte[16];
        Random.Shared.NextBytes(blob);
        byte[] proof1 = new byte[48];
        Random.Shared.NextBytes(proof1);
        byte[] proof2 = new byte[48];
        Random.Shared.NextBytes(proof2);

        byte[]?[] blobs = [blob, null];
        ReadOnlyMemory<byte[]>[] proofs = [new ReadOnlyMemory<byte[]>([proof1, proof2]), default];

        BlobsV2DirectResponse response = new(blobs, proofs, 2);

        // Write via streaming path
        Pipe pipe = new();
        await response.WriteToAsync(pipe.Writer, CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        ReadResult readResult = await pipe.Reader.ReadAsync();
        string streamedJson = Encoding.UTF8.GetString(readResult.Buffer);
        pipe.Reader.AdvanceTo(readResult.Buffer.End);

        // Write via STJ for comparison
        string stjJson = JsonSerializer.Serialize(response, EthereumJsonSerializer.JsonOptions);

        Assert.That(streamedJson, Is.EqualTo(stjJson));
    }

    [Test]
    public async Task BlobsV2DirectResponse_WriteToAsync_empty_list()
    {
        BlobsV2DirectResponse response = new([], [], 0);

        Pipe pipe = new();
        await response.WriteToAsync(pipe.Writer, CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        ReadResult readResult = await pipe.Reader.ReadAsync();
        string json = Encoding.UTF8.GetString(readResult.Buffer);
        pipe.Reader.AdvanceTo(readResult.Buffer.End);

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
        Assert.That(async () => await act(), Throws.InstanceOf<OperationCanceledException>());
    }
}
