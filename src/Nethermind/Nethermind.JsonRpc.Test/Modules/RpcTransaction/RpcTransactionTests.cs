// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.RpcTransaction;

public class RpcTransactionTests
{
    private readonly IJsonSerializer _serializer = new EthereumJsonSerializer();

    public static readonly Transaction[] Transactions =
    [
        .. LegacyTransactionForRpcTests.Transactions,
        .. AccessListTransactionForRpcTests.Transactions,
        .. EIP1559TransactionForRpcTests.Transactions,
        .. BlobTransactionForRpcTests.Transactions,
    ];

    [SetUp]
    public void SetUp()
    {
        AccessListTransactionForRpc.DefaultChainId = BlockchainIds.Mainnet;
    }

    [TestCaseSource(nameof(Transactions))]
    public void Serialized_JSON_satisfies_schema(Transaction transaction)
    {
        TransactionForRpc rpcTransaction = TransactionForRpc.FromTransaction(transaction);
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
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    [TestCaseSource(nameof(Transactions))]
    public void Serialized_JSON_satisfies_Nethermind_fields_schema(Transaction transaction)
    {
        TransactionForRpc rpcTransaction = TransactionForRpc.FromTransaction(transaction);
        string serialized = _serializer.Serialize(rpcTransaction);
        using var jsonDocument = JsonDocument.Parse(serialized);
        JsonElement json = jsonDocument.RootElement;

        json.GetProperty("hash").GetString()?.Should().MatchRegex("^0x[0-9a-fA-F]{64}$");
        json.GetProperty("transactionIndex").GetString()?.Should().MatchRegex("^0x([1-9a-f]+[0-9a-f]*|0)$");
        json.GetProperty("blockHash").GetString()?.Should().MatchRegex("^0x[0-9a-fA-F]{64}$");
        json.GetProperty("blockNumber").GetString()?.Should().MatchRegex("^0x([1-9a-f]+[0-9a-f]*|0)$");
    }
}
