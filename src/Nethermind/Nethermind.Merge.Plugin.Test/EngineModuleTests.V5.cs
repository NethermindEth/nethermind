// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CkzgLib;
using FluentAssertions;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    private const int responseId = 67;

    [TestCase(
        "0x9e205909311e6808bd7167e07bda30bda2b1061127e89e76167781214f3024bf",
        "0x701f48fd56e6ded89a9ec83926eb99eebf9a38b15b4b8f0066574ac1dd9ff6df",
        "0x73cecfc66bc1c8545aa3521e21be51c31bd2054badeeaa781f5fd5b871883f35",
        "0x80ce7f68a5211b5d")]
    public virtual async Task Should_process_block_as_expected_V5(string latestValidHash, string blockHash,
        string stateRoot, string payloadId)
    {
        using MergeTestBlockchain chain =
            await CreateBlockchain(Fork7805.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;
        Hash256 expectedBlockHash = new(blockHash);

        Withdrawal[] withdrawals =
        [
            new Withdrawal { Index = 1, AmountInGwei = 3, Address = TestItem.AddressB, ValidatorIndex = 2 }
        ];
        byte[][] inclusionListRaw = []; // empty inclusion list satisfied by default
        Transaction[] inclusionListTransactions = [];

        string?[] @params = InitForkchoiceParams(chain, inclusionListRaw, withdrawals);
        string response = await RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV3", @params!);
        JsonRpcSuccessResponse? successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

        Assert.Multiple(() =>
        {
            Assert.That(successResponse, Is.Not.Null);
            Assert.That(response, Is.EqualTo(ExpectedValidForkchoiceResponse(chain, payloadId, latestValidHash)));
        });

        response = await RpcTest.TestSerializedRequest(rpc, "engine_getPayloadV4", payloadId);
        successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

        Block block = ExpectedBlock(chain, blockHash, stateRoot, [], inclusionListTransactions, withdrawals, chain.BlockTree.Head!.ReceiptsRoot!, 0);
        Assert.Multiple(() =>
        {
            Assert.That(successResponse, Is.Not.Null);
            Assert.That(response, Is.EqualTo(ExpectedGetPayloadResponse(chain, block, UInt256.Zero)));
        });


        response = await RpcTest.TestSerializedRequest(rpc, "engine_newPayloadV5",
            chain.JsonSerializer.Serialize(ExecutionPayloadV3.Create(block)), "[]", Keccak.Zero.ToString(true), "[]", "[]");
        successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

        string expectedNewPayloadResponse = chain.JsonSerializer.Serialize(new JsonRpcSuccessResponse
        {
            Id = responseId,
            Result = new PayloadStatusV1
            {
                LatestValidHash = expectedBlockHash,
                Status = PayloadStatus.Valid,
                ValidationError = null
            }
        });

        Assert.Multiple(() =>
        {
            Assert.That(successResponse, Is.Not.Null);
            Assert.That(response, Is.EqualTo(expectedNewPayloadResponse));
        });


        var fcuState = new
        {
            headBlockHash = expectedBlockHash.ToString(true),
            safeBlockHash = expectedBlockHash.ToString(true),
            finalizedBlockHash = startingHead.ToString(true)
        };
        @params = [chain.JsonSerializer.Serialize(fcuState), null];

        response = await RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV3", @params!);
        successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

        Assert.Multiple(() =>
        {
            Assert.That(successResponse, Is.Not.Null);
            Assert.That(response, Is.EqualTo(ExpectedValidForkchoiceResponse(chain, null, expectedBlockHash.ToString(true))));
        });
    }

    [Test]
    public async Task Can_get_inclusion_list_V5()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Fork7805.Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;

        Transaction tx1 = Build.A.Transaction
            .WithNonce(0)
            .WithMaxFeePerGas(10.GWei())
            .WithMaxPriorityFeePerGas(2.GWei())
            .WithTo(TestItem.AddressA)
            .SignedAndResolved(TestItem.PrivateKeyB)
            .TestObject;

        Transaction tx2 = Build.A.Transaction
            .WithNonce(1)
            .WithMaxFeePerGas(15.GWei())
            .WithMaxPriorityFeePerGas(3.GWei())
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(TestItem.PrivateKeyB)
            .TestObject;

        chain.TxPool.SubmitTx(tx1, TxHandlingOptions.PersistentBroadcast);
        chain.TxPool.SubmitTx(tx2, TxHandlingOptions.PersistentBroadcast);

        using ArrayPoolList<byte[]>? inclusionList = (await rpc.engine_getInclusionListV1()).Data;

        byte[] tx1Bytes = Rlp.Encode(tx1).Bytes;
        byte[] tx2Bytes = Rlp.Encode(tx2).Bytes;

        Assert.Multiple(() =>
        {
            Assert.That(inclusionList, Is.Not.Null);
            Assert.That(inclusionList.Count, Is.EqualTo(2));
            Assert.That(inclusionList, Does.Contain(tx1Bytes));
            Assert.That(inclusionList, Does.Contain(tx2Bytes));
        });
    }

    [TestCase(
        "0xc07d9fa552b7bac79bf9903a644641c50159d5407a781d4ea574fb55176ad65f",
        "0xaeab64ea7e001370482e6f65ee554a7fb812abb326b09e085b2319e69bdfdf4a")]
    public virtual async Task NewPayloadV5_should_return_invalid_for_unsatisfied_inclusion_list_V5(
        string blockHash,
        string stateRoot)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Fork7805.Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 prevRandao = Keccak.Zero;
        Hash256 startingHead = chain.BlockTree.HeadHash;

        Address feeRecipient = TestItem.AddressC;
        ulong timestamp = Timestamper.UnixTime.Seconds;

        Transaction censoredTx = Build.A.Transaction
            .WithNonce(0)
            .WithMaxFeePerGas(10.GWei())
            .WithMaxPriorityFeePerGas(2.GWei())
            .WithGasLimit(100_000)
            .WithTo(TestItem.AddressA)
            .WithSenderAddress(TestItem.AddressB)
            .SignedAndResolved(TestItem.PrivateKeyB)
            .TestObject;
        byte[][] inclusionListRaw = [Rlp.Encode(censoredTx).Bytes];
        Transaction[] inclusionListTransactions = [censoredTx];

        Hash256 expectedBlockHash = new(blockHash);
        Block block = ExpectedBlock(chain, blockHash, stateRoot, [], inclusionListTransactions, [], chain.BlockTree.Head!.ReceiptsRoot!, 0);
        GetPayloadV4Result expectedPayload = new(block, UInt256.Zero, new BlobsBundleV1(block), [], false);

        string response = await RpcTest.TestSerializedRequest(rpc, "engine_newPayloadV5",
            chain.JsonSerializer.Serialize(ExecutionPayloadV3.Create(block)),
            "[]",
            Keccak.Zero.ToString(true),
            "[]",
            chain.JsonSerializer.Serialize(inclusionListRaw));
        JsonRpcSuccessResponse? successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

        string expectedNewPayloadResponse = chain.JsonSerializer.Serialize(new JsonRpcSuccessResponse
        {
            Id = responseId,
            Result = new PayloadStatusV1
            {
                LatestValidHash = new(blockHash),
                Status = PayloadStatus.InvalidInclusionList
            }
        });

        Assert.Multiple(() =>
        {
            Assert.That(successResponse, Is.Not.Null);
            Assert.That(response, Is.EqualTo(expectedNewPayloadResponse));
        });
    }

    [TestCase(
        "0x9e205909311e6808bd7167e07bda30bda2b1061127e89e76167781214f3024bf",
        "0xb516e35c0108656404d14ddd341bd9730c5bb2e2b426ae158275407b24fc4a81",
        "0xc646c486410b6682874f8e7e978f4944d4947c791a7af740cae6ce8526b1ff0b",
        "0xbb6408787d9389f4",
        "0x642cd2bcdba228efb3996bf53981250d3608289522b80754c4e3c085c93c806f",
        "0x2632e314a000",
        "0x5208")]
    public virtual async Task Should_build_block_with_inclusion_list_transactions_V5(
        string latestValidHash,
        string blockHash,
        string stateRoot,
        string payloadId,
        string receiptsRoot,
        string blockFees,
        string gasUsed)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Fork7805.Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;

        Transaction tx = Build.A.Transaction
            .WithNonce(0)
            .WithMaxFeePerGas(10.GWei())
            .WithMaxPriorityFeePerGas(2.GWei())
            .WithTo(TestItem.AddressA)
            .SignedAndResolved(TestItem.PrivateKeyB)
            .TestObject;
        byte[] txBytes = Rlp.Encode(tx).Bytes;
        byte[][] inclusionListRaw = [txBytes];
        Transaction[] inclusionListTransactions = [tx];

        string?[] @params = InitForkchoiceParams(chain, inclusionListRaw);
        string response = await RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV3", @params!);
        JsonRpcSuccessResponse? successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

        Assert.Multiple(() =>
        {
            Assert.That(successResponse, Is.Not.Null);
            Assert.That(response, Is.EqualTo(ExpectedValidForkchoiceResponse(chain, payloadId, latestValidHash)));
        });

        response = await RpcTest.TestSerializedRequest(rpc, "engine_updatePayloadWithInclusionListV1", payloadId, inclusionListRaw);
        successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

        string expectedUpdatePayloadWithInclusionListResponse = chain.JsonSerializer.Serialize(new JsonRpcSuccessResponse
        {
            Id = responseId,
            Result = payloadId
        });

        Assert.Multiple(() =>
        {
            Assert.That(successResponse, Is.Not.Null);
            Assert.That(response, Is.EqualTo(expectedUpdatePayloadWithInclusionListResponse));
        });

        // Give time to build & proccess before requesting payload
        // Otherwise processing short circuits and IL txs not included
        await Task.Delay(500);

        Block block = ExpectedBlock(
            chain,
            blockHash,
            stateRoot,
            [tx],
            inclusionListTransactions,
            [],
            new Hash256(receiptsRoot),
            Convert.ToInt64(gasUsed, 16));

        response = await RpcTest.TestSerializedRequest(rpc, "engine_getPayloadV4", payloadId);
        successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

        Assert.Multiple(() =>
        {
            Assert.That(successResponse, Is.Not.Null);
            Assert.That(response, Is.EqualTo(ExpectedGetPayloadResponse(chain, block, new UInt256(Convert.ToUInt64(blockFees, 16)))));
        });
    }

    [Test]
    public async Task Can_force_rebuild_payload()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Fork7805.Instance);
        var payloadPreparationService = (PayloadPreparationService)chain.PayloadPreparationService!;

        BlockHeader parentHeader = chain.BlockTree.Head!.Header;
        PayloadAttributes payloadAttributes = new()
        {
            Timestamp = Timestamper.UnixTime.Seconds,
            PrevRandao = Keccak.Zero,
            SuggestedFeeRecipient = TestItem.AddressC,
            Withdrawals = [],
            ParentBeaconBlockRoot = Keccak.Zero
        };

        string payloadId = payloadPreparationService.StartPreparingPayload(parentHeader, payloadAttributes)!;
        uint? buildCount = payloadPreparationService.GetPayloadBuildCount(payloadId);

        Assert.That(buildCount, Is.EqualTo(1));

        payloadPreparationService.ForceRebuildPayload(payloadId);

        await Task.Delay(500);

        buildCount = payloadPreparationService.GetPayloadBuildCount(payloadId);

        Assert.That(buildCount, Is.EqualTo(2));
    }

    private string?[] InitForkchoiceParams(MergeTestBlockchain chain, byte[][] inclusionListTransactions, Withdrawal[]? withdrawals = null)
    {
        Hash256 startingHead = chain.BlockTree.HeadHash;
        Hash256 prevRandao = Keccak.Zero;
        Address feeRecipient = TestItem.AddressC;
        ulong timestamp = Timestamper.UnixTime.Seconds;

        var fcuState = new
        {
            headBlockHash = startingHead.ToString(true),
            safeBlockHash = startingHead.ToString(true),
            finalizedBlockHash = Keccak.Zero.ToString(true)
        };

        var payloadAttrs = new
        {
            timestamp = timestamp.ToHexString(true),
            prevRandao = prevRandao.ToString(),
            suggestedFeeRecipient = feeRecipient.ToString(),
            withdrawals = withdrawals ?? [],
            parentBeaconBlockRoot = Keccak.Zero
        };

        string?[] @params =
        [
            chain.JsonSerializer.Serialize(fcuState),
            chain.JsonSerializer.Serialize(payloadAttrs)
        ];

        return @params;
    }

    private static string ExpectedValidForkchoiceResponse(MergeTestBlockchain chain, string? payloadId, string latestValidHash)
        => chain.JsonSerializer.Serialize(new JsonRpcSuccessResponse
        {
            Id = responseId,
            Result = new ForkchoiceUpdatedV1Result
            {
                PayloadId = payloadId,
                PayloadStatus = new PayloadStatusV1
                {
                    LatestValidHash = new(latestValidHash),
                    Status = PayloadStatus.Valid,
                    ValidationError = null,
                }
            }
        });

    private Block ExpectedBlock(
        MergeTestBlockchain chain,
        string blockHash,
        string stateRoot,
        Transaction[] transactions,
        Transaction[] inclusionListTransactions,
        Withdrawal[] withdrawals,
        Hash256 receiptsRoot,
        long gasUsed)
    {
        Hash256 startingHead = chain.BlockTree.HeadHash;
        Hash256 prevRandao = Keccak.Zero;
        Address feeRecipient = TestItem.AddressC;
        ulong timestamp = Timestamper.UnixTime.Seconds;

        Hash256 expectedBlockHash = new(blockHash);
        Block block = new(
            new(
                startingHead,
                Keccak.OfAnEmptySequenceRlp,
                feeRecipient,
                UInt256.Zero,
                1,
                chain.BlockTree.Head!.GasLimit,
                timestamp,
                Bytes.FromHexString("0x4e65746865726d696e64") // Nethermind
            )
            {
                BlobGasUsed = 0,
                ExcessBlobGas = 0,
                BaseFeePerGas = 0,
                Bloom = Bloom.Empty,
                GasUsed = gasUsed,
                Hash = expectedBlockHash,
                MixHash = prevRandao,
                ParentBeaconBlockRoot = Keccak.Zero,
                ReceiptsRoot = receiptsRoot,
                StateRoot = new(stateRoot),
            },
            transactions,
            Array.Empty<BlockHeader>(),
            withdrawals,
            inclusionListTransactions);

        return block;
    }
    private static string ExpectedGetPayloadResponse(MergeTestBlockchain chain, Block block, UInt256 blockFees)
        => chain.JsonSerializer.Serialize(new JsonRpcSuccessResponse
        {
            Id = responseId,
            Result = new GetPayloadV4Result(block, blockFees, new BlobsBundleV1(block), [], false)
        });
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
        ShardBlobNetworkWrapper wrapper = new ShardBlobNetworkWrapper(getPayloadResultBlobsBundle.Blobs,
            getPayloadResultBlobsBundle.Commitments, getPayloadResultBlobsBundle.Proofs, ProofVersion.V1);
        Assert.That(IBlobProofsManager.For(ProofVersion.V1).ValidateProofs(wrapper), Is.True);
    }

    [Test]
    public async Task GetBlobsV2_should_throw_if_more_than_128_requested_blobs([Values(128, 129)] int requestSize)
    {
        MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Osaka.Instance, mergeConfig: new MergeConfig()
        {
            NewPayloadBlockProcessingTimeout = (int)TimeSpan.FromDays(1).TotalMilliseconds
        });
        IEngineRpcModule rpcModule = chain.EngineRpcModule;

        List<byte[]> request = new List<byte[]>(requestSize);
        for (int i = 0; i < requestSize; i++)
        {
            request.Add(Bytes.FromHexString(i.ToString("X64")));
        }

        ResultWrapper<IEnumerable<BlobAndProofV2>?> result = await rpcModule.engine_getBlobsV2(request.ToArray());

        if (requestSize > 128)
        {
            result.Result.Should().BeEquivalentTo(Result.Fail($"The number of requested blobs must not exceed 128"));
            result.ErrorCode.Should().Be(MergeErrorCodes.TooLargeRequest);
        }
        else
        {
            result.Result.Should().Be(Result.Success);
            result.Data.Should().BeNull();
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

        ResultWrapper<IEnumerable<BlobAndProofV2>?> result = await rpcModule.engine_getBlobsV2([]);

        result.Result.Should().Be(Result.Success);
        result.Data.Should().BeEquivalentTo(ArraySegment<BlobAndProofV2>.Empty);
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
            .WithMaxFeePerGas(1.GWei())
            .WithMaxPriorityFeePerGas(1.GWei())
            .WithMaxFeePerBlobGas(1000.Wei())
            .SignedAndResolved(chain.EthereumEcdsa, TestItem.PrivateKeyA).TestObject;

        chain.TxPool.SubmitTx(blobTx, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);

        ResultWrapper<IEnumerable<BlobAndProofV2>?> result = await rpcModule.engine_getBlobsV2(blobTx.BlobVersionedHashes!);

        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)blobTx.NetworkWrapper!;

        result.Data.Should().NotBeNull();
        result.Data!.Select(static b => b.Blob).Should().BeEquivalentTo(wrapper.Blobs);
        result.Data!.Select(static b => b.Proofs.Length).Should().HaveCount(numberOfBlobs);
        result.Data!.Select(static b => b.Proofs).Should().BeEquivalentTo(wrapper.Proofs.Chunk(128));
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
            .WithMaxFeePerGas(1.GWei())
            .WithMaxPriorityFeePerGas(1.GWei())
            .WithMaxFeePerBlobGas(1000.Wei())
            .SignedAndResolved(chain.EthereumEcdsa, TestItem.PrivateKeyA).TestObject;

        // requesting hashes that are not present in TxPool
        ResultWrapper<IEnumerable<BlobAndProofV2>?> result = await rpcModule.engine_getBlobsV2(blobTx.BlobVersionedHashes!);

        result.Result.Should().Be(Result.Success);
        result.Data.Should().BeNull();
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
            .WithMaxFeePerGas(1.GWei())
            .WithMaxPriorityFeePerGas(1.GWei())
            .WithMaxFeePerBlobGas(1000.Wei())
            .SignedAndResolved(chain.EthereumEcdsa, TestItem.PrivateKeyA).TestObject;

        chain.TxPool.SubmitTx(blobTx, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);

        List<byte[]> blobVersionedHashesRequest = new List<byte[]>(requestSize);

        int actualIndex = 0;
        for (int i = 0; i < requestSize; i++)
        {
            bool addActualHash = i % multiplier == 0;
            blobVersionedHashesRequest.Add(addActualHash ? blobTx.BlobVersionedHashes![actualIndex++]! : Bytes.FromHexString(i.ToString("X64")));
        }

        ResultWrapper<IEnumerable<BlobAndProofV2>?> result = await rpcModule.engine_getBlobsV2(blobVersionedHashesRequest.ToArray());
        if (multiplier > 1)
        {
            result.Result.Should().Be(Result.Success);
            result.Data.Should().BeNull();
        }
        else
        {
            ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)blobTx.NetworkWrapper!;

            result.Data.Should().NotBeNull();
            result.Data!.Select(static b => b.Blob).Should().BeEquivalentTo(wrapper.Blobs);
            result.Data!.Select(static b => b.Proofs.Length).Should().HaveCount(numberOfBlobs);
            result.Data!.Select(static b => b.Proofs).Should().BeEquivalentTo(wrapper.Proofs.Chunk(128));
        }
    }
}
