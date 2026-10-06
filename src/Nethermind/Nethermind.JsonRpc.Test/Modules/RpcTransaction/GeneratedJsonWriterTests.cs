// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Filters;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using NUnit.Framework;
using static Nethermind.Core.Test.Builders.FrameTxTestFrames;

namespace Nethermind.JsonRpc.Test.Modules.RpcTransaction;

/// <summary>
/// The generated writers replace the metadata path for RPC responses, so any byte they write differently changes the wire
/// format; reads must still go through the metadata path unchanged.
/// </summary>
public class GeneratedJsonWriterTests
{
    private static readonly JsonSerializerOptions[] AllOptions =
    [
        EthereumJsonSerializer.JsonOptions,
        EthereumJsonSerializer.JsonOptionsIndented,
        EthereumJsonSerializer.JsonRpcRequestOptions,
    ];

    private static IEnumerable<TestCaseData> Values()
    {
        foreach ((string name, Transaction tx) in Transactions())
        {
            yield return new TestCaseData(TransactionForRpc.FromTransaction(tx, new TransactionForRpcContext(BlockchainIds.Mainnet))).SetArgDisplayNames($"{name}, pending");
            yield return new TestCaseData(TransactionForRpc.FromTransaction(tx, new TransactionForRpcContext(BlockchainIds.Mainnet, TestItem.KeccakB, 25_000_000, 7, 1_700_000_000, 10 * Unit.GWei)))
                .SetArgDisplayNames($"{name}, mined");
        }

        yield return new TestCaseData(new LegacyTransactionForRpc()).SetArgDisplayNames("legacy, all null");
        yield return new TestCaseData(new AccessListTransactionForRpc()).SetArgDisplayNames("access list, all null");
        yield return new TestCaseData(new EIP1559TransactionForRpc()).SetArgDisplayNames("1559, all null");
        yield return new TestCaseData(new BlobTransactionForRpc()).SetArgDisplayNames("blob, all null");
        yield return new TestCaseData(new SetCodeTransactionForRpc()).SetArgDisplayNames("set code, all null");
        yield return new TestCaseData(new FrameTransactionForRpc()).SetArgDisplayNames("frame, all null");
        yield return new TestCaseData(new BlobTransactionForRpc { Blobs = [[1, 2]], Commitments = [], Proofs = [[3]], BlobVersionedHashes = [] })
            .SetArgDisplayNames("blob, network wrapper");
        yield return new TestCaseData(new EIP1559TransactionForRpc { AccessList = AccessListForRpc.FromAccessList(AccessList.Empty), Input = [] })
            .SetArgDisplayNames("1559, empty collections");

        yield return new TestCaseData(new BlockForRpc()).SetArgDisplayNames("block, all default");
        foreach ((string name, Block block) in Blocks())
        {
            yield return new TestCaseData(new BlockForRpc(block, includeFullTransactionData: true, MainnetSpecProvider.Instance)).SetArgDisplayNames($"{name}, full");
            yield return new TestCaseData(new BlockForRpc(block, includeFullTransactionData: false, MainnetSpecProvider.Instance)).SetArgDisplayNames($"{name}, hashes");
        }

        yield return new TestCaseData(new FilterLog(3, 25_000_000, 1_700_000_000, TestItem.KeccakA, 2, TestItem.KeccakB, TestItem.AddressA, [1, 2, 3], [TestItem.KeccakC, TestItem.KeccakD], removed: true))
            .SetArgDisplayNames("log, removed");
        yield return new TestCaseData(new FilterLog(0, 0, 0, null!, 0, null!, null!, null!, null!)).SetArgDisplayNames("log, nulls");
        yield return new TestCaseData(new FilterLog(0, 1, 2, TestItem.KeccakA, 0, TestItem.KeccakB, TestItem.AddressA, [], [])).SetArgDisplayNames("log, empty");
    }

    [TestCaseSource(nameof(Values))]
    public void Generated_writer_writes_the_metadata_path_bytes(object value)
    {
        foreach (JsonSerializerOptions options in AllOptions)
        {
            JsonSerializerOptions metadataOptions = GeneratedJsonWriters.GetMetadataOptions(options);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(GetWriter(value.GetType(), options).IsActive(options), Is.True, "the generated writer deferred to the metadata path");
                if (value is TransactionForRpc)
                {
                    // Serializing as the runtime type reaches the metadata path; as TransactionForRpc it dispatches to the writer.
                    byte[] expected = Serialize(value, value.GetType(), options);
                    Assert.That(Serialize(value, typeof(TransactionForRpc), options), Is.EqualTo(expected));
                    Assert.That(WriteDirectly(GetWriter(value.GetType(), options), value, options), Is.EqualTo(expected));
                }
                else
                {
                    Assert.That(Serialize(value, value.GetType(), options), Is.EqualTo(Serialize(value, value.GetType(), metadataOptions)));
                }
            }
        }
    }

    [TestCaseSource(nameof(Values))]
    public void Reads_match_the_metadata_path(object value)
    {
        Type declared = value is TransactionForRpc ? typeof(TransactionForRpc) : value.GetType();
        foreach (JsonSerializerOptions options in AllOptions)
        {
            JsonSerializerOptions metadataOptions = GeneratedJsonWriters.GetMetadataOptions(options);
            byte[] json = Serialize(value, declared, metadataOptions);

            Assert.That(ReadOutcome(json, declared, options, metadataOptions), Is.EqualTo(ReadOutcome(json, declared, metadataOptions, metadataOptions)));
        }
    }

    // A type the metadata path cannot read (FilterLog has no usable constructor) must fail the same way.
    private static string ReadOutcome(byte[] json, Type type, JsonSerializerOptions readOptions, JsonSerializerOptions metadataOptions)
    {
        try
        {
            object? read = TypeInfoJsonSerializer.Deserialize(json, type, readOptions);
            return Convert.ToHexString(Serialize(read, type, metadataOptions));
        }
        catch (Exception e)
        {
            return $"{e.GetType()}: {e.Message}";
        }
    }

    [TestCase("""{"type":"0x2","nonce":"zz"}""")]
    [TestCase("""{"type":"0x2","gas":[]}""")]
    [TestCase("""{"type":"0x4","authorizationList":{}}""")]
    public void Malformed_requests_fail_as_through_the_metadata_path(string json)
    {
        JsonSerializerOptions options = EthereumJsonSerializer.JsonRpcRequestOptions;
        Exception? expected = Catch(() => TypeInfoJsonSerializer.Deserialize<TransactionForRpc>(json, GeneratedJsonWriters.GetMetadataOptions(options)));
        Exception? actual = Catch(() => TypeInfoJsonSerializer.Deserialize<TransactionForRpc>(json, options));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(expected, Is.Not.Null);
            Assert.That(actual?.GetType(), Is.EqualTo(expected?.GetType()));
            Assert.That(actual?.Message, Is.EqualTo(expected?.Message));
        }
    }

    [Test]
    public void Options_the_writers_cannot_honour_use_the_metadata_path()
    {
        JsonSerializerOptions options = new(EthereumJsonSerializer.JsonOptions) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.WriteAsString };
        EIP1559TransactionForRpc value = (EIP1559TransactionForRpc)TransactionForRpc.FromTransaction(Transactions().First(static t => t.Name == "1559").Tx, new TransactionForRpcContext(BlockchainIds.Mainnet));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetWriter(typeof(EIP1559TransactionForRpc), options).IsActive(options), Is.False);
            Assert.That(Serialize(value, typeof(TransactionForRpc), options), Is.EqualTo(Serialize(value, value.GetType(), options)));
        }
    }

    [Test]
    public void Contracts_customized_by_a_resolver_modifier_use_the_metadata_path()
    {
        DefaultJsonTypeInfoResolver modified = new();
        modified.Modifiers.Add(static info =>
        {
            if (info.Type != typeof(FilterLog)) return;
            foreach (JsonPropertyInfo property in info.Properties)
            {
                if (property.Name == "removed") property.ShouldSerialize = static (_, _) => false;
            }
        });

        JsonSerializerOptions options = new(EthereumJsonSerializer.JsonOptions);
        options.TypeInfoResolverChain.Insert(0, modified);
        FilterLog value = new(3, 25_000_000, 1_700_000_000, TestItem.KeccakA, 2, TestItem.KeccakB, TestItem.AddressA, [1], [TestItem.KeccakC], removed: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetWriter(typeof(FilterLog), options).IsActive(options), Is.False);
            Assert.That(System.Text.Encoding.UTF8.GetString(Serialize(value, typeof(FilterLog), options)), Does.Not.Contain("removed"));
        }
    }

    [Test]
    public void Converter_for_a_wider_type_uses_the_metadata_path()
    {
        JsonSerializerOptions options = new(EthereumJsonSerializer.JsonOptions);
        options.Converters.Insert(0, new WithdrawalsAsObjectConverter());
        BlockForRpc value = new() { Withdrawals = [] };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetWriter(typeof(BlockForRpc), options).IsActive(options), Is.False);
            Assert.That(System.Text.Encoding.UTF8.GetString(Serialize(value, typeof(BlockForRpc), options)), Does.Contain("\"withdrawals\":\"converted\""));
        }
    }

    [Test]
    public void Null_reaches_a_converter_registered_after_the_generated_writer()
    {
        JsonSerializerOptions options = new(EthereumJsonSerializer.JsonOptions);
        options.Converters.Add(new NullHandlingBlockConverter());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(System.Text.Encoding.UTF8.GetString(Serialize(null, typeof(BlockForRpc), options)), Is.EqualTo("\"custom-null\""));
            Assert.That(TypeInfoJsonSerializer.Deserialize<BlockForRpc>("null", options), Is.Not.Null);
        }
    }

    private sealed class NullHandlingBlockConverter : System.Text.Json.Serialization.JsonConverter<BlockForRpc>
    {
        public override bool HandleNull => true;

        public override BlockForRpc Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new();

        public override void Write(Utf8JsonWriter writer, BlockForRpc value, JsonSerializerOptions options) => writer.WriteStringValue("custom-null");
    }

    [Test]
    public void Block_transactions_keep_the_serializer_depth_limit()
    {
        BlockForRpc block = new(Blocks().First().Block, includeFullTransactionData: false, MainnetSpecProvider.Instance);
        JsonSerializerOptions options = new(EthereumJsonSerializer.JsonOptions) { MaxDepth = 2 };

        Assert.That(() => Serialize(block, typeof(BlockForRpc), options), Throws.InstanceOf<JsonException>());
    }

    // STJ adapts a converter for a wider type by casting; a generated writer cannot call it as JsonConverter<Withdrawal[]>.
    private sealed class WithdrawalsAsObjectConverter : System.Text.Json.Serialization.JsonConverter<object>
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(Withdrawal[]);

        public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options) => writer.WriteStringValue("converted");
    }

    [Test]
    public void Naming_policy_is_applied_at_run_time()
    {
        JsonSerializerOptions options = new(EthereumJsonSerializer.JsonOptions) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        BlockForRpc value = new(Blocks().First().Block, includeFullTransactionData: true, MainnetSpecProvider.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetWriter(typeof(BlockForRpc), options).IsActive(options), Is.True);
            Assert.That(Serialize(value, value.GetType(), options), Is.EqualTo(Serialize(value, value.GetType(), GeneratedJsonWriters.GetMetadataOptions(options))));
        }
    }

    private static IEnumerable<(string Name, Transaction Tx)> Transactions()
    {
        AccessList accessList = new AccessList.Builder().AddAddress(TestItem.AddressC).AddStorage(UInt256.One).AddStorage(2).Build();

        yield return ("legacy", Build.A.Transaction.WithType(TxType.Legacy).WithNonce(1).WithGasPrice(7).WithTo(TestItem.AddressB).WithValue(5).SignedAndResolved().TestObject);
        yield return ("legacy, pre-155", Build.A.Transaction.WithType(TxType.Legacy).WithChainId(null).WithData([1, 2]).SignedAndResolved(TestItem.PrivateKeyA, isEip155Enabled: false).TestObject);
        yield return ("legacy, create", Build.A.Transaction.WithType(TxType.Legacy).WithTo(null).WithCode([0x60, 0x00]).SignedAndResolved().TestObject);
        yield return ("access list", Build.A.Transaction.WithType(TxType.AccessList).WithChainId(BlockchainIds.Mainnet).WithAccessList(accessList).SignedAndResolved().TestObject);
        yield return ("1559", Build.A.Transaction.WithType(TxType.EIP1559).WithChainId(BlockchainIds.Mainnet).WithMaxFeePerGas(30).WithMaxPriorityFeePerGas(2).WithData([9]).SignedAndResolved().TestObject);
        yield return ("blob", Build.A.Transaction.WithType(TxType.Blob).WithChainId(BlockchainIds.Mainnet).WithShardBlobTxTypeAndFields(2).WithMaxFeePerGas(30).SignedAndResolved().TestObject);
        yield return ("set code", Build.A.Transaction.WithType(TxType.SetCode).WithChainId(BlockchainIds.Mainnet).WithAuthorizationCodeIfAuthorizationListTx().WithMaxFeePerGas(30).SignedAndResolved().TestObject);
        yield return ("frame", FrameTx(SelfVerify(PrefixFrameGas), OnlyVerify()));
    }

    private static IEnumerable<(string Name, Block Block)> Blocks()
    {
        Transaction[] transactions = [.. Transactions().Where(static t => t.Tx.Type != TxType.FrameTx).Select(static t => t.Tx)];
        yield return ("block, cancun", Build.A.Block.WithNumber(25_000_000).WithBaseFeePerGas(7).WithTransactions(transactions)
            .WithWithdrawals(Build.A.Withdrawal.WithIndex(1).WithValidatorIndex(2).WithRecipient(TestItem.AddressB).WithAmount(3).TestObject)
            .WithBlobGasUsed(131072).WithExcessBlobGas(0).WithParentBeaconBlockRoot(TestItem.KeccakE).TestObject);
        yield return ("block, pre-merge", Build.A.Block.WithNumber(1).WithDifficulty(17).WithTotalDifficulty(17L).WithUncles(Build.A.BlockHeader.TestObject).TestObject);
        yield return ("block, empty", Build.A.Block.WithNumber(2).WithTransactions([]).WithWithdrawals([]).TestObject);
    }

    private static IGeneratedJsonWriter GetWriter(Type type, JsonSerializerOptions options) =>
        options.Converters.OfType<IGeneratedJsonWriter>().SingleOrDefault(w => w.WrittenType == type)
        ?? (GeneratedJsonWriters.TryGetDispatchWriter(type, out IGeneratedJsonWriter? writer) ? writer : throw new AssertionException($"no generated writer for {type.Name}"));

    private static byte[] WriteDirectly(IGeneratedJsonWriter writer, object value, JsonSerializerOptions options)
    {
        System.Buffers.ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter json = new(buffer, new JsonWriterOptions { Encoder = options.Encoder, Indented = options.WriteIndented, NewLine = options.NewLine }))
        {
            writer.WriteValue(json, value, options);
        }

        return buffer.WrittenSpan.ToArray();
    }

    [Test]
    public void Transaction_writers_are_reached_only_through_the_dispatch()
    {
        using (Assert.EnterMultipleScope())
        {
            foreach (JsonSerializerOptions options in AllOptions)
            {
                Assert.That(options.Converters.OfType<IGeneratedJsonWriter>().Select(static w => w.WrittenType), Is.EquivalentTo(new[] { typeof(BlockForRpc), typeof(FilterLog) }));
            }

            foreach (Type type in new[] { typeof(LegacyTransactionForRpc), typeof(AccessListTransactionForRpc), typeof(EIP1559TransactionForRpc), typeof(BlobTransactionForRpc), typeof(SetCodeTransactionForRpc), typeof(FrameTransactionForRpc) })
            {
                Assert.That(GeneratedJsonWriters.TryGetDispatchWriter(type, out _), Is.True, type.Name);
            }
        }
    }

    private static byte[] Serialize(object? value, Type type, JsonSerializerOptions options) => TypeInfoJsonSerializer.SerializeToUtf8Bytes(value, type, options);

    private static Exception? Catch(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }
}
