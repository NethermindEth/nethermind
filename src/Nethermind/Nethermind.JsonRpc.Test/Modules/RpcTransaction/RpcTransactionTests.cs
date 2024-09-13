// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.RpcTransaction;

public class RpcTransactionTests
{
    private readonly IJsonSerializer _serializer = new EthereumJsonSerializer([
        new IRpcTransaction.JsonConverter()
            .RegisterTransactionType(TxType.Legacy, typeof(RpcLegacyTransaction))
            .RegisterTransactionType(TxType.AccessList, typeof(RpcAccessListTransaction))
            .RegisterTransactionType(TxType.EIP1559, typeof(RpcEIP1559Transaction))
            .RegisterTransactionType(TxType.Blob, typeof(RpcBlobTransaction))
    ]);

    private readonly ITransactionConverter<IRpcTransaction> _converter = new IRpcTransaction.TransactionConverter()
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
    public void Always_satisfies_schema(Transaction transaction)
    {
        IRpcTransaction rpcTransaction = _converter.FromTransaction(transaction);
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

        // TODO: Test that implementors satisfy the schema for the base `RpcNethermindTransaction`
    }

    [TestCaseSource(nameof(Transactions))]
    public void RpcTransaction_JSON_roundtrip(Transaction tx)
    {
        IRpcTransaction rpcTx = _converter.FromTransaction(tx);
        string serialized = _serializer.Serialize(rpcTx);
        IRpcTransaction deserialized = _serializer.Deserialize<IRpcTransaction>(serialized);

        rpcTx.Should().BeEquivalentTo(deserialized, options => options.RespectingRuntimeTypes());
    }
}
