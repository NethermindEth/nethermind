// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Serialization.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.RpcTransaction;

public class TransactionForRpcTests
{
    private readonly IJsonSerializer _serializer = new EthereumJsonSerializer();

    public static readonly ulong SomeChainId = 123ul;

    public static readonly Transaction[] Transactions =
    [
        .. LegacyTransactionForRpcTests.Transactions,
        .. AccessListTransactionForRpcTests.Transactions,
        .. EIP1559TransactionForRpcTests.Transactions,
        .. BlobTransactionForRpcTests.Transactions,
        .. SetCodeTransactionForRpcTests.Transactions,
    ];

    [Test]
    public void R_and_s_are_quantity_and_not_data()
    {
        byte[] r = new byte[32];
        byte[] s = new byte[32];
        r[1] = 1;
        s[2] = 2;

        Transaction tx = new()
        {
            Signature = new Signature(r, s, 27)
        };

        var txForRpc = TransactionForRpc.FromTransaction(tx);

        EthereumJsonSerializer serializer = new();
        string serialized = serializer.Serialize(txForRpc);

        var json = JObject.Parse(serialized);
        var expectedS = JObject.Parse("""{ "s": "0x20000000000000000000000000000000000000000000000000000000000"}""");
        var expectedR = JObject.Parse("""{ "r": "0x1000000000000000000000000000000000000000000000000000000000000"}""");

        json.Should().ContainSubtree(expectedS);
        json.Should().ContainSubtree(expectedR);
    }

    [TestCaseSource(nameof(Transactions))]
    public void Serialized_JSON_satisfies_schema(Transaction transaction)
    {
        TransactionForRpc rpcTransaction = TransactionForRpc.FromTransaction(transaction, chainId: SomeChainId);
        string serialized = _serializer.Serialize(rpcTransaction);
        using var jsonDocument = JsonDocument.Parse(serialized);
        JsonElement json = jsonDocument.RootElement;

        switch (transaction.Type)
        {
            case TxType.Legacy:
                LegacyTransactionForRpcTests.ValidateSchema(json);
                break;
            case TxType.AccessList:
                AccessListTransactionForRpcTests.ValidateSchema(json);
                break;
            case TxType.EIP1559:
                EIP1559TransactionForRpcTests.ValidateSchema(json);
                break;
            case TxType.Blob:
                BlobTransactionForRpcTests.ValidateSchema(json);
                break;
            case TxType.SetCode:
                SetCodeTransactionForRpcTests.ValidateSchema(json);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    [TestCaseSource(nameof(Transactions))]
    public void Serialized_JSON_satisfies_Nethermind_fields_schema(Transaction transaction)
    {
        TransactionForRpc rpcTransaction = TransactionForRpc.FromTransaction(transaction, chainId: SomeChainId);
        string serialized = _serializer.Serialize(rpcTransaction);
        using var jsonDocument = JsonDocument.Parse(serialized);
        JsonElement json = jsonDocument.RootElement;

        json.GetProperty("hash").GetString()?.Should().MatchRegex("^0x[0-9a-fA-F]{64}$");
        json.GetProperty("transactionIndex").GetString()?.Should().MatchRegex("^0x([1-9a-f]+[0-9a-f]*|0)$");
        json.GetProperty("blockHash").GetString()?.Should().MatchRegex("^0x[0-9a-fA-F]{64}$");
        json.GetProperty("blockNumber").GetString()?.Should().MatchRegex("^0x([1-9a-f]+[0-9a-f]*|0)$");
    }
}
