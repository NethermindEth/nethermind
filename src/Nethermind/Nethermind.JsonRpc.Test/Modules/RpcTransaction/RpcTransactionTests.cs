// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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
    private readonly IJsonSerializer _serializer = new EthereumJsonSerializer([
        new RpcNethermindTransaction.JsonConverter()
            .RegisterTransactionType(TxType.Legacy, typeof(RpcLegacyTransaction))
            .RegisterTransactionType(TxType.AccessList, typeof(RpcAccessListTransaction))
            .RegisterTransactionType(TxType.EIP1559, typeof(RpcEIP1559Transaction))
            .RegisterTransactionType(TxType.Blob, typeof(RpcBlobTransaction))
    ]);

    private readonly IFromTransaction<RpcNethermindTransaction> _converter = new RpcNethermindTransaction.TransactionConverter()
        .RegisterConverter(TxType.Legacy, RpcLegacyTransaction.Converter)
        .RegisterConverter(TxType.AccessList, RpcAccessListTransaction.Converter)
        .RegisterConverter(TxType.EIP1559, RpcEIP1559Transaction.Converter)
        .RegisterConverter(TxType.Blob, RpcBlobTransaction.Converter);

    public static readonly Transaction[] Transactions =
    [
        .. RpcLegacyTransactionTests.Transactions,
        .. RpcAccessListTransactionTests.Transactions,
        .. RpcEIP1559TransactionTests.Transactions,
        .. RpcBlobTransactionTests.Transactions,
    ];

    [SetUp]
    public void SetUp()
    {
        RpcAccessListTransaction.DefaultChainId = BlockchainIds.Mainnet;
    }

    [TestCaseSource(nameof(Transactions))]
    public void Serialized_JSON_satisfies_schema(Transaction transaction)
    {
        RpcNethermindTransaction rpcTransaction = _converter.FromTransaction(transaction);
        string serialized = _serializer.Serialize(rpcTransaction);
        using var jsonDocument = JsonDocument.Parse(serialized);
        JsonElement json = jsonDocument.RootElement;

        switch (transaction.Type)
        {
            case TxType.Legacy:
                RpcLegacyTransactionTests.ValidateSchema(json);
                break;
            case TxType.AccessList:
                RpcAccessListTransactionTests.ValidateSchema(json);
                break;
            case TxType.EIP1559:
                RpcEIP1559TransactionTests.ValidateSchema(json);
                break;
            case TxType.Blob:
                RpcBlobTransactionTests.ValidateSchema(json);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    [TestCaseSource(nameof(Transactions))]
    public void Serialized_JSON_satisfies_Nethermind_fields_schema(Transaction transaction)
    {
        RpcNethermindTransaction rpcTransaction = _converter.FromTransaction(transaction);
        string serialized = _serializer.Serialize(rpcTransaction);
        using var jsonDocument = JsonDocument.Parse(serialized);
        JsonElement json = jsonDocument.RootElement;

        json.GetProperty("hash").GetString()?.Should().MatchRegex("^0x[0-9a-fA-F]{64}$");
        json.GetProperty("transactionIndex").GetString()?.Should().MatchRegex("^0x([1-9a-f]+[0-9a-f]*|0)$");
        json.GetProperty("blockHash").GetString()?.Should().MatchRegex("^0x[0-9a-fA-F]{64}$");
        json.GetProperty("blockNumber").GetString()?.Should().MatchRegex("^0x([1-9a-f]+[0-9a-f]*|0)$");
    }

    [Test]
    public void RpcNethermindTransactionJsonConverter_only_supports_registering_subclasses()
    {
        var converter = new RpcNethermindTransaction.JsonConverter();
        Action action = () => converter.RegisterTransactionType(default, typeof(object));
        action.Should().Throw<ArgumentException>();
    }
}
