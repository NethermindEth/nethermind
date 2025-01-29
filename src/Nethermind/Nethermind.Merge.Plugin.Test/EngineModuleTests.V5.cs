// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test;
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
        "0x9233c931ff3c17ae124b9aa2ca8db1c641a2dd87fa2d7e00030b274bcc33f928",
        "0xe97fdbfa2fcf60073d9579d87b127cdbeffbe6c7387b9e1e836eb7f8fb2d9548",
        "0xa272b2f949e4a0e411c9b45542bd5d0ef3c311b5f26c4ed6b7a8d4f605a91154",
        "0xa90e8b68e4923ef7")]
    public virtual async Task Should_process_block_as_expected_V5(string latestValidHash, string blockHash,
        string stateRoot, string payloadId)
    {
        using MergeTestBlockchain chain =
            await CreateBlockchain(Osaka.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = CreateEngineModule(chain);
        Hash256 startingHead = chain.BlockTree.HeadHash;
        Hash256 expectedBlockHash = new(blockHash);

        Withdrawal[] withdrawals =
        [
            new Withdrawal { Index = 1, AmountInGwei = 3, Address = TestItem.AddressB, ValidatorIndex = 2 }
        ];
        byte[][] inclusionListTransactions = []; // empty inclusion list satisfied by default

        string?[] @params = InitForkchoiceParams(chain, inclusionListTransactions, withdrawals);
        string response = await RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV4", @params!);
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

        response = await RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV4", @params!);
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
        using MergeTestBlockchain chain = await CreateBlockchain(Osaka.Instance);
        IEngineRpcModule rpc = CreateEngineModule(chain);

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

        byte[][]? inclusionList = (await rpc.engine_getInclusionList()).Data;
        inclusionList.Should().NotBeEmpty();
        inclusionList.Length.Should().Be(2);

        byte[] tx1Bytes = Rlp.Encode(tx1).Bytes;
        byte[] tx2Bytes = Rlp.Encode(tx2).Bytes;
        Assert.Multiple(() =>
        {
            Assert.That(inclusionList[0].SequenceEqual(tx1Bytes));
            Assert.That(inclusionList[1].SequenceEqual(tx2Bytes));
        });
    }

    [TestCase(
        "0x2bc9c183553124a0f95ae47b35660f7addc64f2f0eb2d03f7f774085f0ed8117",
        "0x692ba034d9dc8c4c2d7d172a2fb1f3773f8a250fde26501b99d2733a2b48e70b",
        "0x651832fe5119239f")]
    public async Task NewPayloadV5_should_reject_block_with_unsatisfied_inclusion_list_V5(string blockHash, string stateRoot, string payloadId)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Osaka.Instance);
        IEngineRpcModule rpc = CreateEngineModule(chain);
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
        byte[][] inclusionListTransactions = [Rlp.Encode(censoredTx).Bytes];

        Hash256 expectedBlockHash = new(blockHash);
        Block block = ExpectedBlock(chain, blockHash, stateRoot, [], inclusionListTransactions, [], chain.BlockTree.Head!.ReceiptsRoot!, 0);
        GetPayloadV4Result expectedPayload = new(block, UInt256.Zero, new BlobsBundleV1(block), executionRequests: []);

        string response = await RpcTest.TestSerializedRequest(rpc, "engine_newPayloadV5",
            chain.JsonSerializer.Serialize(ExecutionPayloadV3.Create(block)),
            "[]",
            Keccak.Zero.ToString(true),
            "[]",
            chain.JsonSerializer.Serialize(inclusionListTransactions));
        JsonRpcSuccessResponse? successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

        string expectedNewPayloadResponse = chain.JsonSerializer.Serialize(new JsonRpcSuccessResponse
        {
            Id = responseId,
            Result = new PayloadStatusV1
            {
                LatestValidHash = Keccak.Zero,
                Status = PayloadStatus.Invalid,
                ValidationError = "Block excludes valid inclusion list transaction"
            }
        });

        Assert.Multiple(() =>
        {
            Assert.That(successResponse, Is.Not.Null);
            Assert.That(response, Is.EqualTo(expectedNewPayloadResponse));
        });
    }

    [TestCase(
        "0x9233c931ff3c17ae124b9aa2ca8db1c641a2dd87fa2d7e00030b274bcc33f928",
        "0x6ee90247ca4b3cc8092f032a1c4b30e878797eb12c9852a598aa561410eb31bf",
        "0x3c3e0bb8ade764491e6073541192a076b10e0f550c3ba6635a8f48cc9cc96996",
        "0x17812ce24578c28c",
        "0x642cd2bcdba228efb3996bf53981250d3608289522b80754c4e3c085c93c806f",
        "0x2632e314a000",
        "0x5208")]
    public async Task Should_build_block_with_inclusion_list_transactions_V5(
        string latestValidHash,
        string blockHash,
        string stateRoot,
        string payloadId,
        string receiptsRoot,
        string blockFees,
        string gasUsed)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Osaka.Instance);
        IEngineRpcModule rpc = CreateEngineModule(chain);

        Transaction tx = Build.A.Transaction
            .WithNonce(0)
            .WithMaxFeePerGas(10.GWei())
            .WithMaxPriorityFeePerGas(2.GWei())
            .WithTo(TestItem.AddressA)
            .SignedAndResolved(TestItem.PrivateKeyB)
            .TestObject;
        byte[] txBytes = Rlp.Encode(tx).Bytes;
        byte[][] inclusionListTransactions = [txBytes];

        string?[] @params = InitForkchoiceParams(chain, inclusionListTransactions);
        string response = await RpcTest.TestSerializedRequest(rpc, "engine_forkchoiceUpdatedV4", @params!);
        JsonRpcSuccessResponse? successResponse = chain.JsonSerializer.Deserialize<JsonRpcSuccessResponse>(response);

        Assert.Multiple(() =>
        {
            Assert.That(successResponse, Is.Not.Null);
            Assert.That(response, Is.EqualTo(ExpectedValidForkchoiceResponse(chain, payloadId, latestValidHash)));
        });

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
            parentBeaconBlockRoot = Keccak.Zero,
            inclusionListTransactions
        };

        string?[] @params =
        [
            chain.JsonSerializer.Serialize(fcuState),
            chain.JsonSerializer.Serialize(payloadAttrs)
        ];

        return @params;
    }

    private static string ExpectedValidForkchoiceResponse(MergeTestBlockchain chain, string? payloadId, string latestValidHash)
        => chain.JsonSerializer.Serialize(new JsonRpcSuccessResponse {
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
        byte[][] inclusionListTransactions,
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
            Result = new GetPayloadV4Result(block, blockFees, new BlobsBundleV1(block), executionRequests: [])
        });
}
