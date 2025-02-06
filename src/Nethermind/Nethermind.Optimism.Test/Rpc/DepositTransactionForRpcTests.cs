// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using NUnit.Framework;
using Nethermind.Serialization.Json;
using Nethermind.Optimism.Rpc;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Core;
using FluentAssertions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Crypto;
using System;
using System.Collections.Generic;

namespace Nethermind.Optimism.Test.Rpc;

public class DepositTransactionForRpcTests
{
    private readonly IJsonSerializer serializer = new EthereumJsonSerializer();

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
    public void SetUp()
    {
        TransactionForRpc.RegisterTransactionType<DepositTransactionForRpc>();
    }

    [TestCaseSource(nameof(Transactions))]
    public void Always_satisfies_schema(Transaction transaction)
    {
        TransactionForRpc rpcTransaction = TransactionForRpc.FromTransaction(transaction);
        string serialized = serializer.Serialize(rpcTransaction);
        using var jsonDocument = JsonDocument.Parse(serialized);
        JsonElement json = jsonDocument.RootElement;
        ValidateSchema(json);
    }

    private static void ValidateSchema(JsonElement json)
    {
        json.GetProperty("type").GetString().Should().MatchRegex("^0x7[eE]$");
        json.GetProperty("sourceHash").GetString().Should().MatchRegex("^0x[0-9a-fA-F]{64}$");
        json.GetProperty("from").GetString().Should().MatchRegex("^0x[0-9a-fA-F]{40}$");
        json.GetProperty("to").GetString()?.Should().MatchRegex("^0x[0-9a-fA-F]{40}$");
        var hasMint = json.TryGetProperty("mint", out var mint);
        if (hasMint)
        {
            mint.GetString()?.Should().MatchRegex("^0x([1-9a-f]+[0-9a-f]*|0)$");
        }
        json.GetProperty("value").GetString().Should().MatchRegex("^0x([1-9a-f]+[0-9a-f]*|0)$");
        json.GetProperty("gas").GetString().Should().MatchRegex("^0x([1-9a-f]+[0-9a-f]*|0)$");
        var hasIsSystemTx = json.TryGetProperty("isSystemTx", out var isSystemTx);
        if (hasIsSystemTx)
        {
            isSystemTx.GetBoolean();
        }
        json.GetProperty("input").GetString().Should().MatchRegex("^0x[0-9a-f]*$");
        json.GetProperty("nonce").GetString().Should().MatchRegex("^0x([1-9a-f]+[0-9a-f]*|0)$");
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
        var rpcTx = serializer.Deserialize<DepositTransactionForRpc>(testCase.json);
        rpcTx.Should().NotBeNull();

        var toTransaction = rpcTx.ToTransaction;
        toTransaction.Should().Throw<ArgumentNullException>().WithParameterName(testCase.missingField);
    }

    private static readonly IEnumerable<(string, string)> ValidJsonTransactions = [
        (nameof(DepositTransactionForRpc.Mint), """{"type":"0x7e","nonce":null,"gas": "0x1234", "gasPrice":null,"maxPriorityFeePerGas":null,"maxFeePerGas":null,"value":"0x1","input":"0x616263646566","v":null,"r":null,"s":null,"to":null,"sourceHash":"0x0000000000000000000000000000000000000000000000000000000000000000","from":"0x0000000000000000000000000000000000000001","isSystemTx":false,"hash":"0xa4341f3db4363b7ca269a8538bd027b2f8784f84454ca917668642d5f6dffdf9"}"""),
        (nameof(DepositTransactionForRpc.IsSystemTx), """{"type":"0x7e","nonce":null,"gas": "0x1234", "gasPrice":null,"maxPriorityFeePerGas":null,"maxFeePerGas":null,"value":"0x1","input":"0x616263646566","v":null,"r":null,"s":null,"to":null,"sourceHash":"0x0000000000000000000000000000000000000000000000000000000000000000","from":"0x0000000000000000000000000000000000000001","hash":"0xa4341f3db4363b7ca269a8538bd027b2f8784f84454ca917668642d5f6dffdf9"}"""),
    ];

    [TestCaseSource(nameof(ValidJsonTransactions))]
    public void Accepts_valid_transaction_missing_field((string missingField, string json) testCase)
    {
        var rpcTx = serializer.Deserialize<DepositTransactionForRpc>(testCase.json);
        rpcTx.Should().NotBeNull();

        var toTransaction = rpcTx.ToTransaction;
        toTransaction.Should().NotThrow();
    }
}
