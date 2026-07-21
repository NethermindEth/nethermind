// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using NUnit.Framework;
using Nethermind.Serialization.Json;
using Nethermind.Optimism.Rpc;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Crypto;
using System;
using System.Collections.Generic;

namespace Nethermind.Optimism.Test.Rpc;

public class DepositTransactionForRpcTests
{
    private readonly IJsonSerializer _serializer = new EthereumJsonSerializer();

    private static TransactionBuilder<Transaction> Build => Core.Test.Builders.Build.A.Transaction.WithType(TxType.DepositTx);
    public static readonly Transaction[] Transactions = [
        Build.TestObject,
        Build
            .With(static s => s.IsOPSystemTransaction = true)
            .With(static s => s.Mint = 1234)
            .TestObject,
        Build
            .WithGasLimit(0x1234)
            .WithValue(0x1)
            .WithData([0x61, 0x62, 0x63, 0x64, 0x65, 0x66])
            .WithSourceHash(Hash256.Zero)
            .WithSenderAddress(Address.FromNumber(1))
            .WithHash(new Hash256("0xa4341f3db4363b7ca269a8538bd027b2f8784f84454ca917668642d5f6dffdf9"))
            .TestObject
    ];

    [SetUp]
    public void SetUp() =>
        TransactionForRpc.RegisterTransactionType<DepositTransactionForRpc>();

    [TestCaseSource(nameof(Transactions))]
    public void Always_satisfies_schema(Transaction transaction)
    {
        TransactionForRpc rpcTransaction = TransactionForRpc.FromTransaction(transaction);
        string serialized = _serializer.Serialize(rpcTransaction);
        using JsonDocument jsonDocument = JsonDocument.Parse(serialized);
        JsonElement json = jsonDocument.RootElement;
        ValidateSchema(json);
    }

    private static void ValidateSchema(JsonElement json)
    {
        Assert.That(json.GetProperty("type").GetString(), Does.Match("^0x7[eE]$"));
        Assert.That(json.GetProperty("sourceHash").GetString(), Does.Match("^0x[0-9a-fA-F]{64}$"));
        Assert.That(json.GetProperty("from").GetString(), Does.Match("^0x[0-9a-fA-F]{40}$"));
        Assert.That(json.GetProperty("to").GetString(), Does.Match("^0x[0-9a-fA-F]{40}$"));
        bool hasMint = json.TryGetProperty("mint", out JsonElement mint);
        if (hasMint)
        {
            Assert.That(mint.GetString(), Does.Match("^0x([1-9a-f]+[0-9a-f]*|0)$"));
        }
        Assert.That(json.GetProperty("value").GetString(), Does.Match("^0x([1-9a-f]+[0-9a-f]*|0)$"));
        Assert.That(json.GetProperty("gas").GetString(), Does.Match("^0x([1-9a-f]+[0-9a-f]*|0)$"));
        bool hasIsSystemTx = json.TryGetProperty("isSystemTx", out JsonElement isSystemTx);
        if (hasIsSystemTx)
        {
            isSystemTx.GetBoolean();
        }
        Assert.That(json.GetProperty("input").GetString(), Does.Match("^0x[0-9a-f]*$"));
        Assert.That(json.GetProperty("nonce").GetString(), Does.Match("^0x([1-9a-f]+[0-9a-f]*|0)$"));
    }

    private static readonly IEnumerable<(string, string)> MalformedJsonTransactions = [
        (nameof(DepositTransactionForRpc.Gas), """{"type":"0x7e","nonce":null,"gasPrice":null,"maxPriorityFeePerGas":null,"maxFeePerGas":null,"value":"0x1","input":"0x616263646566","v":null,"r":null,"s":null,"to":null,"sourceHash":"0x0000000000000000000000000000000000000000000000000000000000000000","from":"0x0000000000000000000000000000000000000001","isSystemTx":false,"hash":"0xa4341f3db4363b7ca269a8538bd027b2f8784f84454ca917668642d5f6dffdf9"}"""),
        (nameof(DepositTransactionForRpc.Value), """{"type":"0x7e","nonce":null,"gas": "0x1234", "gasPrice":null,"maxPriorityFeePerGas":null,"maxFeePerGas":null,"input":"0x616263646566","v":null,"r":null,"s":null,"to":null,"sourceHash":"0x0000000000000000000000000000000000000000000000000000000000000000","from":"0x0000000000000000000000000000000000000001","isSystemTx":false,"hash":"0xa4341f3db4363b7ca269a8538bd027b2f8784f84454ca917668642d5f6dffdf9"}"""),
        (nameof(DepositTransactionForRpc.Input), """{"type":"0x7e","nonce":null,"gas": "0x1234", "gasPrice":null,"maxPriorityFeePerGas":null,"maxFeePerGas":null,"value":"0x1","v":null,"r":null,"s":null,"to":null,"sourceHash":"0x0000000000000000000000000000000000000000000000000000000000000000","from":"0x0000000000000000000000000000000000000001","isSystemTx":false,"hash":"0xa4341f3db4363b7ca269a8538bd027b2f8784f84454ca917668642d5f6dffdf9"}"""),
        (nameof(DepositTransactionForRpc.From), """{"type":"0x7e","nonce":null,"gas": "0x1234", "gasPrice":null,"maxPriorityFeePerGas":null,"maxFeePerGas":null,"value":"0x1","input":"0x616263646566","v":null,"r":null,"s":null,"to":null,"sourceHash":"0x0000000000000000000000000000000000000000000000000000000000000000","isSystemTx":false,"hash":"0xa4341f3db4363b7ca269a8538bd027b2f8784f84454ca917668642d5f6dffdf9"}"""),
        (nameof(DepositTransactionForRpc.SourceHash), """{"type":"0x7e","nonce":null,"gas": "0x1234", "gasPrice":null,"maxPriorityFeePerGas":null,"maxFeePerGas":null,"value":"0x1","input":"0x616263646566","v":null,"r":null,"s":null,"to":null,"from":"0x0000000000000000000000000000000000000001","isSystemTx":false,"hash":"0xa4341f3db4363b7ca269a8538bd027b2f8784f84454ca917668642d5f6dffdf9"}"""),
    ];

    [TestCaseSource(nameof(MalformedJsonTransactions))]
    public void Rejects_malformed_transaction_missing_field((string missingField, string json) testCase)
    {
        DepositTransactionForRpc rpcTx = _serializer.Deserialize<DepositTransactionForRpc>(testCase.json);
        Assert.That(rpcTx, Is.Not.Null);

        Func<Result<Transaction>> toTransaction = () => rpcTx.ToTransaction();
        Assert.That(toTransaction, Throws.TypeOf<ArgumentNullException>().With.Property(nameof(ArgumentException.ParamName)).EqualTo(testCase.missingField));
    }

    private static readonly IEnumerable<(string, string)> ValidJsonTransactions = [
        (nameof(DepositTransactionForRpc.Mint), """{"type":"0x7e","nonce":null,"gas": "0x1234", "gasPrice":null,"maxPriorityFeePerGas":null,"maxFeePerGas":null,"value":"0x1","input":"0x616263646566","v":null,"r":null,"s":null,"to":null,"sourceHash":"0x0000000000000000000000000000000000000000000000000000000000000000","from":"0x0000000000000000000000000000000000000001","isSystemTx":false,"hash":"0xa4341f3db4363b7ca269a8538bd027b2f8784f84454ca917668642d5f6dffdf9"}"""),
        (nameof(DepositTransactionForRpc.IsSystemTx), """{"type":"0x7e","nonce":null,"gas": "0x1234", "gasPrice":null,"maxPriorityFeePerGas":null,"maxFeePerGas":null,"value":"0x1","input":"0x616263646566","v":null,"r":null,"s":null,"to":null,"sourceHash":"0x0000000000000000000000000000000000000000000000000000000000000000","from":"0x0000000000000000000000000000000000000001","hash":"0xa4341f3db4363b7ca269a8538bd027b2f8784f84454ca917668642d5f6dffdf9"}"""),
    ];

    [TestCaseSource(nameof(ValidJsonTransactions))]
    public void Accepts_valid_transaction_missing_field((string missingField, string json) testCase)
    {
        DepositTransactionForRpc rpcTx = _serializer.Deserialize<DepositTransactionForRpc>(testCase.json);
        Assert.That(rpcTx, Is.Not.Null);

        Func<Result<Transaction>> toTransaction = () => rpcTx.ToTransaction();
        Assert.That(toTransaction, Throws.Nothing);
    }

    [Test]
    public void Rejects_deserialization_when_declared_as_user_input_transaction()
    {
        const string json = """{"type":"0x7e","gas":"0x1234","value":"0x1","input":"0x616263646566","to":null,"sourceHash":"0x0000000000000000000000000000000000000000000000000000000000000000","from":"0x0000000000000000000000000000000000000001","isSystemTx":false}""";

        Assert.That(() => _serializer.Deserialize<SignableTransactionForRpc>(json),
            Throws.InstanceOf<JsonException>(),
            "deposit transactions are output-only and must be rejected as input where the declared type is SignableTransactionForRpc");
    }

    private static DepositTransactionForRpc DepositTxWithGas(ulong? gas) => new()
    {
        SourceHash = Hash256.Zero,
        From = Address.Zero,
        Value = 0,
        Input = [],
        Gas = gas,
    };

    [TestCase(5_000UL, null, 5_000UL)]
    [TestCase(5_000UL, 0UL, 5_000UL)]
    [TestCase(5_000UL, 1_000UL, 1_000UL)]
    [TestCase(5_000UL, 10_000UL, 5_000UL)]
    [TestCase(null, 1_000UL, 1_000UL)]
    public void ToTransaction_caps_and_defaults_gas(ulong? gas, ulong? gasCap, ulong expectedGasLimit)
    {
        DepositTransactionForRpc rpcTx = DepositTxWithGas(gas);

        Transaction tx = (Transaction)rpcTx.ToTransaction(gasCap: gasCap);

        Assert.That(tx.GasLimit, Is.EqualTo(expectedGasLimit));
    }

    [TestCase(null, null)]
    [TestCase(null, 0UL)]
    public void ToTransaction_throws_when_gas_missing_and_no_cap(ulong? gas, ulong? gasCap)
    {
        DepositTransactionForRpc rpcTx = DepositTxWithGas(gas);

        Func<Result<Transaction>> toTransaction = () => rpcTx.ToTransaction(gasCap: gasCap);

        Assert.That(toTransaction, Throws.TypeOf<ArgumentNullException>().With.Property(nameof(ArgumentException.ParamName)).EqualTo(nameof(DepositTransactionForRpc.Gas)));
    }
}
