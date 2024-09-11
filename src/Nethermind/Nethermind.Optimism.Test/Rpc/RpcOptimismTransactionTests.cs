// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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

namespace Nethermind.Optimism.Test.Rpc;

public class RpcOptimismTransactionTests
{
    private readonly IJsonSerializer _serializer = new EthereumJsonSerializer([IRpcTransaction.JsonConverter]);

    private readonly IRpcTransactionConverter _converter = new ComposeTransactionConverter()
        .RegisterConverter(TxType.DepositTx, RpcOptimismTransaction.Converter);

    private static TransactionBuilder<Transaction> Build => Core.Test.Builders.Build.A.Transaction.WithType(TxType.DepositTx);
    public static readonly Transaction[] Transactions = [
        Build.TestObject,
        Build
            .With(s => s.IsOPSystemTransaction = true)
            .With(s => s.Mint = 1234)
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

    [TestCaseSource(nameof(Transactions))]
    public void Always_satisfies_schema(Transaction transaction)
    {
        IRpcTransaction rpcTransaction = _converter.FromTransaction(transaction, new OptimismTxReceipt());
        string serialized = _serializer.Serialize(rpcTransaction);
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
    }
}
