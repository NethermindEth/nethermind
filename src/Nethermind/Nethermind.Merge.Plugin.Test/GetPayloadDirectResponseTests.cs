// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Buffers;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.SszRest;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

[TestFixture]
public class GetPayloadDirectResponseTests
{
    public enum BalKind
    {
        None,
        Encoded,
        Decoded
    }

    public enum PayloadBodiesEndpoint
    {
        ByHash,
        ByRange
    }

    public static IEnumerable<TestCaseData> JsonParityCases()
    {
        int[] versions = [5, 6];
        int[] txCounts = [0, 1, 64, 256];
        int[] blobCounts = [0, 1, 6];
        bool[] bools = [false, true];
        BalKind[] balKinds = [BalKind.None, BalKind.Encoded, BalKind.Decoded];
        ulong?[] slotNumbers = [null, 42UL];

        for (int versionIndex = 0; versionIndex < versions.Length; versionIndex++)
            for (int txIndex = 0; txIndex < txCounts.Length; txIndex++)
                for (int blobIndex = 0; blobIndex < blobCounts.Length; blobIndex++)
                    for (int withdrawalsIndex = 0; withdrawalsIndex < bools.Length; withdrawalsIndex++)
                        for (int requestsIndex = 0; requestsIndex < bools.Length; requestsIndex++)
                        {
                            int version = versions[versionIndex];
                            if (version == 5)
                            {
                                yield return new TestCaseData(version, txCounts[txIndex], blobCounts[blobIndex], bools[withdrawalsIndex], bools[requestsIndex], BalKind.None, null);
                                continue;
                            }

                            for (int balIndex = 0; balIndex < balKinds.Length; balIndex++)
                                for (int slotIndex = 0; slotIndex < slotNumbers.Length; slotIndex++)
                                {
                                    yield return new TestCaseData(version, txCounts[txIndex], blobCounts[blobIndex], bools[withdrawalsIndex], bools[requestsIndex], balKinds[balIndex], slotNumbers[slotIndex]);
                                }
                        }
    }

    [TestCaseSource(nameof(JsonParityCases))]
    public async Task Direct_response_json_matches_dto_json(
        int version,
        int txCount,
        int blobCount,
        bool withdrawals,
        bool requests,
        BalKind balKind,
        ulong? slotNumber)
    {
        (Block block, BlobsBundleV2 blobsBundle, byte[][]? executionRequests) = CreatePayloadInputs(txCount, blobCount, withdrawals, requests, balKind, slotNumber);
        object plain = CreatePlainResult(version, block, blobsBundle, executionRequests);
        IStreamableResult direct = CreateDirectResult(version, block, blobsBundle, executionRequests);

        byte[] expected = JsonSerializer.SerializeToUtf8Bytes(plain, plain.GetType(), EthereumJsonSerializer.JsonOptions);
        byte[] actual = await WriteStreamableAsync(direct);

        Assert.That(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)), Is.True);
    }

    public static IEnumerable<TestCaseData> PayloadBodiesJsonParityCases()
    {
        int[] versions = [1, 2];
        PayloadBodiesEndpoint[] endpoints = [PayloadBodiesEndpoint.ByHash, PayloadBodiesEndpoint.ByRange];
        int[] txCounts = [0, 1, 64];
        bool[] bools = [false, true];

        for (int endpointIndex = 0; endpointIndex < endpoints.Length; endpointIndex++)
            for (int versionIndex = 0; versionIndex < versions.Length; versionIndex++)
                for (int txIndex = 0; txIndex < txCounts.Length; txIndex++)
                    for (int withdrawalsIndex = 0; withdrawalsIndex < bools.Length; withdrawalsIndex++)
                        for (int includeNullIndex = 0; includeNullIndex < bools.Length; includeNullIndex++)
                            for (int balIndex = 0; balIndex < bools.Length; balIndex++)
                            {
                                int version = versions[versionIndex];
                                bool blockAccessList = bools[balIndex];
                                if (version == 1 && blockAccessList)
                                {
                                    continue;
                                }

                                yield return new TestCaseData(
                                    version,
                                    endpoints[endpointIndex],
                                    txCounts[txIndex],
                                    bools[withdrawalsIndex],
                                    bools[includeNullIndex],
                                    blockAccessList);
                            }
    }

    [TestCaseSource(nameof(PayloadBodiesJsonParityCases))]
    public async Task Payload_bodies_direct_response_json_matches_dto_json(
        int version,
        PayloadBodiesEndpoint endpoint,
        int txCount,
        bool withdrawals,
        bool includeNull,
        bool blockAccessList)
    {
        Assert.That(endpoint, Is.AnyOf(PayloadBodiesEndpoint.ByHash, PayloadBodiesEndpoint.ByRange));

        Transaction[] transactions = CreateTransactions(txCount);
        Withdrawal[]? withdrawalItems = withdrawals ? CreateWithdrawals(2) : null;

        if (version == 1)
        {
            ExecutionPayloadBodyV1Result?[] plain = CreatePayloadBodiesV1(transactions, withdrawalItems, includeNull);
            await AssertStreamableJsonMatchesPlainAsync(plain, new PayloadBodiesV1DirectResponse(plain));
            return;
        }

        byte[]? blockAccessListBytes = blockAccessList ? [1, 2, 3] : null;
        ExecutionPayloadBodyV2Result?[] expected = CreatePayloadBodiesV2(transactions, withdrawalItems, blockAccessListBytes, includeNull);
        PayloadBodiesV2DirectResponse.PayloadBody?[] items = CreatePayloadBodiesV2DirectItems(transactions, withdrawalItems, blockAccessListBytes, includeNull);

        using PayloadBodiesV2DirectResponse direct = new(items);
        await AssertStreamableJsonMatchesPlainAsync(expected, direct);
    }

    [TestCase(1, false, -1)]
    [TestCase(1, false, 0)]
    [TestCase(1, false, 2)]
    [TestCase(2, false, -1)]
    [TestCase(2, false, 0)]
    [TestCase(2, false, 2)]
    [TestCase(2, true, 2)]
    public async Task Payload_bodies_raw_block_rlp_response_matches_dto_json_and_ssz(int version, bool blockAccessList, int withdrawalCount)
    {
        Transaction[] transactions =
        [
            CreateTransaction(TxType.Legacy),
            CreateTransaction(TxType.EIP1559),
            CreateTransaction(TxType.Blob),
            CreateTransaction(TxType.SetCode)
        ];
        Withdrawal[]? withdrawals = withdrawalCount < 0 ? null : CreateWithdrawals(withdrawalCount);
        Block block = CreateBlock(transactions, withdrawals, requests: null, BalKind.None, slotNumber: null);
        byte[] blockRlp = Rlp.Encode(block).Bytes;

        if (version == 1)
        {
            ExecutionPayloadBodyV1Result?[] expected =
            [
                new(transactions, withdrawals),
                null
            ];
            PayloadBodiesV1DirectResponse.PayloadBody?[] items =
            [
                PayloadBodiesV1DirectResponse.CreatePayloadBody(blockRlp),
                null
            ];
            PayloadBodiesV1DirectResponse direct = new(items);

            await AssertStreamableJsonMatchesPlainAsync(expected, direct);

            ArrayBufferWriter<byte> expectedSsz = new();
            ArrayBufferWriter<byte> actualSsz = new();
            SszCodec.EncodePayloadBodiesV1Response(expected, expectedSsz);
            SszCodec.EncodePayloadBodiesV1Response(direct, actualSsz);

            Assert.That(actualSsz.WrittenSpan.ToArray(), Is.EqualTo(expectedSsz.WrittenSpan.ToArray()));
            return;
        }

        byte[]? blockAccessListBytes = blockAccessList ? [1, 2, 3] : null;
        ExecutionPayloadBodyV2Result?[] expectedV2 =
        [
            new(transactions, withdrawals, blockAccessListBytes),
            null
        ];
        PayloadBodiesV2DirectResponse.PayloadBody?[] itemsV2 =
        [
            PayloadBodiesV2DirectResponse.CreatePayloadBody(blockRlp, ArrayMemoryManager.From(blockAccessListBytes)),
            null
        ];

        using PayloadBodiesV2DirectResponse directV2 = new(itemsV2);
        await AssertStreamableJsonMatchesPlainAsync(expectedV2, directV2);

        ArrayBufferWriter<byte> expectedV2Ssz = new();
        ArrayBufferWriter<byte> actualV2Ssz = new();
        SszCodec.EncodePayloadBodiesV2Response(expectedV2, expectedV2Ssz);
        SszCodec.EncodePayloadBodiesV2Response(directV2, actualV2Ssz);

        Assert.That(actualV2Ssz.WrittenSpan.ToArray(), Is.EqualTo(expectedV2Ssz.WrittenSpan.ToArray()));
    }

    [Test]
    public void Payload_bodies_v1_indexer_throws_argument_out_of_range()
    {
        PayloadBodiesV1DirectResponse direct = new([new ExecutionPayloadBodyV1Result([], null)]);

        Assert.That(() => { ExecutionPayloadBodyV1Result? _ = direct[-1]; }, Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => { ExecutionPayloadBodyV1Result? _ = direct[direct.Count]; }, Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [TestCase(5)]
    [TestCase(6)]
    public async Task Json_rpc_envelope_matches_plain_dto_semantically(int version)
    {
        (Block block, BlobsBundleV2 blobsBundle, byte[][]? executionRequests) = CreatePayloadInputs(1, 1, withdrawals: true, requests: true, BalKind.Encoded, slotNumber: 42);
        byte[] expected = version == 5
            ? await WriteResponseAsync(ResultWrapper<GetPayloadV5Result?>.Success((GetPayloadV5Result)CreatePlainResult(version, block, blobsBundle, executionRequests)))
            : await WriteResponseAsync(ResultWrapper<GetPayloadV6Result?>.Success((GetPayloadV6Result)CreatePlainResult(version, block, blobsBundle, executionRequests)));
        byte[] actual = version == 5
            ? await WriteResponseAsync(ResultWrapper<GetPayloadV5Result?>.Success((GetPayloadV5Result)CreateDirectResult(version, block, blobsBundle, executionRequests)))
            : await WriteResponseAsync(ResultWrapper<GetPayloadV6Result?>.Success((GetPayloadV6Result)CreateDirectResult(version, block, blobsBundle, executionRequests)));

        Assert.That(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)), Is.True);
    }

    [Test]
    public async Task Json_rpc_envelope_cancellation_does_not_complete_partial_direct_response()
    {
        (Block block, BlobsBundleV2 blobsBundle, byte[][]? executionRequests) = CreatePayloadInputs(1, 1, withdrawals: true, requests: true, BalKind.Encoded, slotNumber: 42);
        JsonRpcResponse response = ResultWrapper<GetPayloadV5Result?>.Success((GetPayloadV5Result)CreateDirectResult(5, block, blobsBundle, executionRequests));
        using MemoryStream stream = new();
        PipeWriter writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
        using CancellationTokenSource cancellationTokenSource = new();
        cancellationTokenSource.Cancel();

        try
        {
            Func<Task> act = async () => await JsonRpcResponseWriter.WriteAsync(writer, response, EthereumJsonSerializer.JsonOptions, cancellationTokenSource.Token);

            Assert.That(async () => await act(), Throws.TypeOf<OperationCanceledException>());
            await writer.FlushAsync(CancellationToken.None);

            string partialResponse = Encoding.UTF8.GetString(stream.ToArray());
            Assert.That(partialResponse, Does.Contain("\"result\":"));
            Assert.That(partialResponse, Does.Not.Contain(",\"id\":"));
        }
        finally
        {
            await writer.CompleteAsync();
        }
    }

    [TestCase(typeof(GetPayloadV5Result), true)]
    [TestCase(typeof(GetPayloadV6Result), true)]
    [TestCase(typeof(object), true)]
    public void CanBeStreamable_is_true(Type type, bool expected) =>
        Assert.That(GetCanBeStreamable(type), Is.EqualTo(expected));

    [TestCase(5)]
    [TestCase(6)]
    public void Direct_response_ssz_matches_plain_dto(int version)
    {
        (Block block, BlobsBundleV2 blobsBundle, byte[][]? executionRequests) = CreatePayloadInputs(2, 0, withdrawals: true, requests: true, BalKind.Encoded, slotNumber: 42);
        ArrayBufferWriter<byte> expected = new();
        ArrayBufferWriter<byte> actual = new();

        if (version == 5)
        {
            SszCodec.EncodeGetPayloadV5Response((GetPayloadV5Result)CreatePlainResult(version, block, blobsBundle, executionRequests), expected);
            SszCodec.EncodeGetPayloadV5Response((GetPayloadV5Result)CreateDirectResult(version, block, blobsBundle, executionRequests), actual);
        }
        else
        {
            SszCodec.EncodeGetPayloadV6Response((GetPayloadV6Result)CreatePlainResult(version, block, blobsBundle, executionRequests), expected);
            SszCodec.EncodeGetPayloadV6Response((GetPayloadV6Result)CreateDirectResult(version, block, blobsBundle, executionRequests), actual);
        }

        Assert.That(actual.WrittenSpan.ToArray(), Is.EqualTo(expected.WrittenSpan.ToArray()));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ExecutionPayload_materialization_depends_on_path(bool sszPath)
    {
        (Block block, BlobsBundleV2 blobsBundle, byte[][]? executionRequests) = CreatePayloadInputs(1, 0, withdrawals: false, requests: true, BalKind.None, slotNumber: null);
        CountingGetPayloadV5DirectResponse response = new(block, UInt256.One, blobsBundle, executionRequests!, shouldOverrideBuilder: false);

        if (sszPath)
        {
            ArrayBufferWriter<byte> writer = new();
            SszCodec.EncodeGetPayloadV5Response(response, writer);
        }
        else
        {
            await WriteStreamableAsync(response);
        }

        Assert.That(response.ExecutionPayloadReadCount, Is.EqualTo(sszPath ? 1 : 0));
    }

    public static IEnumerable<TestCaseData> TransactionCases()
    {
        yield return new TestCaseData(CreateTransaction(TxType.Legacy)).SetName("Legacy");
        yield return new TestCaseData(CreateTransaction(TxType.AccessList)).SetName("AccessList");
        yield return new TestCaseData(CreateTransaction(TxType.EIP1559)).SetName("EIP1559");
        yield return new TestCaseData(CreateTransaction(TxType.Blob)).SetName("Blob");
        yield return new TestCaseData(CreateTransaction(TxType.SetCode)).SetName("SetCode");
    }

    [TestCaseSource(nameof(TransactionCases))]
    public async Task Pooled_transaction_writer_matches_rlp(Transaction transaction)
    {
        Block block = CreateBlock([transaction], withdrawals: [], requests: null, BalKind.None, slotNumber: null);
        BlobsBundleV2 blobsBundle = new([], [], []);
        GetPayloadV5DirectResponse direct = new(block, UInt256.Zero, blobsBundle, executionRequests: [], shouldOverrideBuilder: false);
        byte[] actualJson = await WriteStreamableAsync(direct);
        JsonNode transactionNode = JsonNode.Parse(actualJson)!["executionPayload"]!["transactions"]![0]!;
        byte[] expectedRlp = Rlp.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes;

        Assert.That(transactionNode.GetValue<string>(), Is.EqualTo(expectedRlp.ToHexString(true)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Encoded_transactions_fast_path_matches_reencoding_path(bool includeV6Fields)
    {
        Transaction[] transactions =
        [
            CreateTransaction(TxType.Legacy),
            CreateTransaction(TxType.EIP1559),
            CreateTransaction(TxType.SetCode)
        ];
        Block block = CreateBlock(transactions, withdrawals: [], requests: null, BalKind.None, slotNumber: includeV6Fields ? 42UL : null);
        BlobsBundleV2 blobsBundle = new([], [], []);
        byte[][] executionRequests = [];
        int version = includeV6Fields ? 6 : 5;

        block.EncodedTransactions = Array.ConvertAll(transactions, static tx => Rlp.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes);
        byte[] fastPathJson = await WriteStreamableAsync(CreateDirectResult(version, block, blobsBundle, executionRequests));

        block.EncodedTransactions = null;
        byte[] reencodingPathJson = await WriteStreamableAsync(CreateDirectResult(version, block, blobsBundle, executionRequests));

        Assert.That(JsonNode.DeepEquals(JsonNode.Parse(fastPathJson), JsonNode.Parse(reencodingPathJson)), Is.True);
    }

    [TestCase(null, false)]
    [TestCase(0, true)]
    [TestCase(2, true)]
    public async Task Withdrawals_property_is_omitted_only_when_null(int? withdrawalCount, bool expectedPresent)
    {
        Withdrawal[]? withdrawals = withdrawalCount is null ? null : CreateWithdrawals(withdrawalCount.GetValueOrDefault());
        Block block = CreateBlock([], withdrawals, requests: null, BalKind.None, slotNumber: null);
        GetPayloadV5DirectResponse direct = new(block, UInt256.Zero, new BlobsBundleV2([], [], []), executionRequests: [], shouldOverrideBuilder: false);
        byte[] json = await WriteStreamableAsync(direct);
        JsonObject executionPayload = JsonNode.Parse(json)!["executionPayload"]!.AsObject();

        Assert.That(executionPayload.ContainsKey("withdrawals"), Is.EqualTo(expectedPresent));
    }

    private static object CreatePlainResult(int version, Block block, BlobsBundleV2 blobsBundle, byte[][]? executionRequests) =>
        version == 5
            ? new GetPayloadV5Result(block, UInt256.One, blobsBundle, executionRequests!, shouldOverrideBuilder: true)
            : new GetPayloadV6Result(block, UInt256.One, blobsBundle, executionRequests!, shouldOverrideBuilder: true);

    private static IStreamableResult CreateDirectResult(int version, Block block, BlobsBundleV2 blobsBundle, byte[][]? executionRequests) =>
        version == 5
            ? new GetPayloadV5DirectResponse(block, UInt256.One, blobsBundle, executionRequests!, shouldOverrideBuilder: true)
            : new GetPayloadV6DirectResponse(block, UInt256.One, blobsBundle, executionRequests!, shouldOverrideBuilder: true);

    private static (Block Block, BlobsBundleV2 BlobsBundle, byte[][]? ExecutionRequests) CreatePayloadInputs(
        int txCount,
        int blobCount,
        bool withdrawals,
        bool requests,
        BalKind balKind,
        ulong? slotNumber)
    {
        Transaction[] transactions = new Transaction[txCount];
        for (int i = 0; i < transactions.Length; i++)
        {
            transactions[i] = Build.A.Transaction
                .WithNonce((ulong)i)
                .WithValue(i + 1)
                .TestObject;
        }

        byte[][]? executionRequests = requests ? CreateByteArrays(3, 8, seed: 80) : null;
        Block block = CreateBlock(transactions, withdrawals ? CreateWithdrawals(2) : null, executionRequests, balKind, slotNumber);
        BlobsBundleV2 blobsBundle = new(
            CreateByteArrays(blobCount, 48, seed: 1),
            CreateByteArrays(blobCount, 64, seed: 20),
            CreateByteArrays(blobCount, 48, seed: 40));

        return (block, blobsBundle, executionRequests);
    }

    private static Block CreateBlock(
        Transaction[] transactions,
        Withdrawal[]? withdrawals,
        byte[][]? requests,
        BalKind balKind,
        ulong? slotNumber)
    {
        Block block = Build.A.Block
            .WithPostMergeRules()
            .WithNumber(12UL)
            .WithTimestamp(1234)
            .WithParentHash(TestItem.KeccakA)
            .WithBeneficiary(TestItem.AddressB)
            .WithStateRoot(TestItem.KeccakB)
            .WithReceiptsRoot(TestItem.KeccakC)
            .WithMixHash(TestItem.KeccakD)
            .WithBloom(Bloom.Empty)
            .WithExtraData([1, 2, 3])
            .WithBaseFeePerGas(7)
            .WithGasLimit(30_000_000)
            .WithGasUsed((ulong)transactions.Length * Transaction.BaseTxGasCost)
            .WithParentBeaconBlockRoot(TestItem.KeccakE)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .WithTransactions(transactions)
            .WithWithdrawals(withdrawals)
            .WithSlotNumber(slotNumber)
            .TestObject;

        block.ExecutionRequests = requests;
        if (balKind is not BalKind.None)
        {
            ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithPrecompileChanges(block.ParentHash!, block.Timestamp).TestObject;
            if (balKind == BalKind.Encoded)
            {
                block.EncodedBlockAccessList = Rlp.Encode(bal).Bytes;
            }
            else
            {
                block.BlockAccessList = bal;
            }
        }

        return block;
    }

    private static Withdrawal[] CreateWithdrawals(int count)
    {
        Withdrawal[] withdrawals = new Withdrawal[count];
        for (int i = 0; i < count; i++)
        {
            withdrawals[i] = new Withdrawal
            {
                Index = (ulong)i,
                ValidatorIndex = (ulong)(100 + i),
                Address = TestItem.AddressA,
                AmountInGwei = (ulong)(1_000 + i)
            };
        }
        return withdrawals;
    }

    private static byte[][] CreateByteArrays(int count, int length, int seed)
    {
        byte[][] items = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            byte[] item = new byte[length];
            for (int j = 0; j < item.Length; j++)
            {
                item[j] = (byte)(seed + i + j);
            }
            items[i] = item;
        }
        return items;
    }

    private static Transaction CreateTransaction(TxType type)
    {
        TransactionBuilder<Transaction> builder = Build.A.Transaction
            .WithType(type)
            .WithNonce((ulong)type + 1)
            .WithChainId(TestBlockchainIds.ChainId)
            .WithMaxFeePerGas(10.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .WithAccessList(AccessList.Empty);

        if (type == TxType.Blob)
        {
            builder.WithShardBlobTxTypeAndFields(blobCount: 1, spec: Osaka.Instance);
        }

        if (type == TxType.SetCode)
        {
            builder.WithAuthorizationCodeIfAuthorizationListTx();
        }

        return builder.SignedAndResolved(TestItem.PrivateKeyA).TestObject;
    }

    private static Transaction[] CreateTransactions(int count)
    {
        Transaction[] transactions = new Transaction[count];
        for (int i = 0; i < transactions.Length; i++)
        {
            transactions[i] = Build.A.Transaction
                .WithNonce((ulong)i)
                .WithValue(i + 1)
                .TestObject;
        }

        return transactions;
    }

    private static ExecutionPayloadBodyV1Result?[] CreatePayloadBodiesV1(
        Transaction[] transactions,
        Withdrawal[]? withdrawals,
        bool includeNull) =>
        includeNull
            ? [new ExecutionPayloadBodyV1Result(transactions, withdrawals), null, new ExecutionPayloadBodyV1Result([], null)]
            : [new ExecutionPayloadBodyV1Result(transactions, withdrawals)];

    private static ExecutionPayloadBodyV2Result?[] CreatePayloadBodiesV2(
        Transaction[] transactions,
        Withdrawal[]? withdrawals,
        byte[]? blockAccessList,
        bool includeNull) =>
        includeNull
            ? [new ExecutionPayloadBodyV2Result(transactions, withdrawals, blockAccessList), null, new ExecutionPayloadBodyV2Result([], null, null)]
            : [new ExecutionPayloadBodyV2Result(transactions, withdrawals, blockAccessList)];

    private static PayloadBodiesV2DirectResponse.PayloadBody?[] CreatePayloadBodiesV2DirectItems(
        Transaction[] transactions,
        Withdrawal[]? withdrawals,
        byte[]? blockAccessList,
        bool includeNull) =>
        includeNull
            ? [PayloadBodiesV2DirectResponse.CreatePayloadBody(transactions, withdrawals, ArrayMemoryManager.From(blockAccessList)), null, PayloadBodiesV2DirectResponse.CreatePayloadBody([], null, null)]
            : [PayloadBodiesV2DirectResponse.CreatePayloadBody(transactions, withdrawals, ArrayMemoryManager.From(blockAccessList))];

    private static bool GetCanBeStreamable(Type type)
    {
        Type shape = typeof(ResultWrapper<>).Assembly
            .GetType("Nethermind.JsonRpc.RpcPayloadTypeShape`1", throwOnError: true)!
            .MakeGenericType(type);
        return (bool)shape.GetField("CanBeStreamable", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
    }

    private static async Task<byte[]> WriteStreamableAsync(IStreamableResult result)
    {
        using MemoryStream stream = new();
        PipeWriter writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
        await result.WriteToAsync(writer, CancellationToken.None);
        await writer.FlushAsync();
        await writer.CompleteAsync();
        return stream.ToArray();
    }

    private static async Task AssertStreamableJsonMatchesPlainAsync<TItem>(IReadOnlyList<TItem?> plain, IStreamableResult direct)
        where TItem : class
    {
        byte[] expected = JsonSerializer.SerializeToUtf8Bytes(plain, plain.GetType(), EthereumJsonSerializer.JsonOptions);
        byte[] actual = await WriteStreamableAsync(direct);

        Assert.That(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)), Is.True);
    }

    private static async Task<byte[]> WriteResponseAsync(JsonRpcResponse response)
    {
        using MemoryStream stream = new();
        PipeWriter writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
        await JsonRpcResponseWriter.WriteAsync(writer, response, EthereumJsonSerializer.JsonOptions, CancellationToken.None);
        await writer.FlushAsync();
        await writer.CompleteAsync();
        return stream.ToArray();
    }

    private sealed class CountingGetPayloadV5DirectResponse(
        Block block,
        UInt256 blockFees,
        BlobsBundleV2 blobsBundle,
        byte[][] executionRequests,
        bool shouldOverrideBuilder)
        : GetPayloadV5Result(block, blockFees, blobsBundle, executionRequests, shouldOverrideBuilder), IStreamableResult
    {
        public int ExecutionPayloadReadCount { get; private set; }

        public override ExecutionPayloadV3 ExecutionPayload
        {
            get
            {
                ExecutionPayloadReadCount++;
                return base.ExecutionPayload;
            }
        }

        public ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken) =>
            GetPayloadDirectResponseWriter.WriteV5Async(writer, Block, BlockValue, BlobsBundle, ExecutionRequests, ShouldOverrideBuilder, cancellationToken);
    }
}
