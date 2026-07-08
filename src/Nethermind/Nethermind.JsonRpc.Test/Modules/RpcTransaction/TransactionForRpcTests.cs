// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
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

        TransactionForRpc txForRpc = TransactionForRpc.FromTransaction(tx);

        EthereumJsonSerializer serializer = new();
        string serialized = serializer.Serialize(txForRpc);
        JToken json = JToken.Parse(serialized);

        Assert.That(json.Value<string>("s"), Is.EqualTo("0x20000000000000000000000000000000000000000000000000000000000"));
        Assert.That(json.Value<string>("r"), Is.EqualTo("0x1000000000000000000000000000000000000000000000000000000000000"));
    }

    [TestCase("""{"type":"0x0","gasPrice":"0x1"}""", typeof(LegacyTransactionForRpc))]
    [TestCase("""{"maxFeePerGas":"0x1","maxPriorityFeePerGas":"0x1"}""", typeof(EIP1559TransactionForRpc))]
    [TestCase("""{"blobVersionedHashes":[]}""", typeof(BlobTransactionForRpc))]
    public void Deserializes_polymorphically_when_declared_as_LegacyTransactionForRpc(string json, Type expectedType)
    {
        LegacyTransactionForRpc tx = _serializer.Deserialize<LegacyTransactionForRpc>(json);

        Assert.That(tx, Is.TypeOf(expectedType),
            "input parameters declared as LegacyTransactionForRpc must still dispatch to the concrete tx type");
    }

    [TestCaseSource(nameof(Transactions))]
    public void Serialized_JSON_satisfies_schema(Transaction transaction)
    {
        TransactionForRpc rpcTransaction = TransactionForRpc.FromTransaction(transaction, new(SomeChainId));
        string serialized = _serializer.Serialize(rpcTransaction);
        using JsonDocument jsonDocument = JsonDocument.Parse(serialized);
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
                throw new ArgumentOutOfRangeException(nameof(transaction), transaction.Type, "Unknown transaction type.");
        }
    }

    [TestCaseSource(nameof(Transactions))]
    public void Serialized_JSON_satisfies_Nethermind_fields_schema(Transaction transaction)
    {
        TransactionForRpc rpcTransaction = TransactionForRpc.FromTransaction(transaction, new(SomeChainId));
        string serialized = _serializer.Serialize(rpcTransaction);
        using JsonDocument jsonDocument = JsonDocument.Parse(serialized);
        JsonElement json = jsonDocument.RootElement;

        Assert.That(json.GetProperty("hash").GetString(), Is.Null.Or.Matches("^0x[0-9a-fA-F]{64}$"));
        Assert.That(json.GetProperty("transactionIndex").GetString(), Is.Null.Or.Matches("^0x([1-9a-f]+[0-9a-f]*|0)$"));
        Assert.That(json.GetProperty("blockHash").GetString(), Is.Null.Or.Matches("^0x[0-9a-fA-F]{64}$"));
        Assert.That(json.GetProperty("blockNumber").GetString(), Is.Null.Or.Matches("^0x([1-9a-f]+[0-9a-f]*|0)$"));
    }

    [Test]
    public void Legacy_transaction_should_populate_chainId_from_signature_when_transaction_chainId_is_null()
    {
        Transaction tx = new()
        {
            Type = TxType.Legacy,
            Nonce = 0x9a,
            To = new Address("0x7435ed30a8b4aeb0877cef0c6e8cffe834eb865f"),
            Value = 0,
            GasLimit = 0x11c32,
            GasPrice = 0x5763d65,
            Data = null,
            ChainId = null,
            Signature = new Signature(
                new UInt256(Bytes.FromHexString("0x551fe45ccebb0318196e31dbc60da87c43dc60b8fb01afb3286693fa09878730"), true),
                new UInt256(Bytes.FromHexString("0x40d33e9afecfe1516b045d61a3272bddbc83f482a7f2c749311248b50fe62e81"), true),
                0x18e5bb3abd109ful
            )
        };

        TransactionForRpc rpcTx = TransactionForRpc.FromTransaction(tx);

        Assert.That(rpcTx, Is.TypeOf<LegacyTransactionForRpc>());
        LegacyTransactionForRpc legacyRpcTx = (LegacyTransactionForRpc)rpcTx;

        ulong? expectedChainId = tx.Signature.ChainId;
        Assert.That(expectedChainId, Is.EqualTo(0xc72dd9d5e883eul));
        Assert.That(legacyRpcTx.ChainId, Is.EqualTo(expectedChainId));
    }

    [Test]
    public void Legacy_transaction_should_use_transaction_chainId_when_present()
    {
        ulong explicitChainId = 1ul;
        Transaction tx = new()
        {
            Type = TxType.Legacy,
            Nonce = 1,
            To = new Address("0x7435ed30a8b4aeb0877cef0c6e8cffe834eb865f"),
            Value = 0,
            GasLimit = 21000,
            GasPrice = 100,
            Data = null,
            ChainId = explicitChainId,
            Signature = new Signature(
                new UInt256(Bytes.FromHexString("0x551fe45ccebb0318196e31dbc60da87c43dc60b8fb01afb3286693fa09878730"), true),
                new UInt256(Bytes.FromHexString("0x40d33e9afecfe1516b045d61a3272bddbc83f482a7f2c749311248b50fe62e81"), true),
                0x18e5bb3abd109ful
            )
        };

        TransactionForRpc rpcTx = TransactionForRpc.FromTransaction(tx);

        Assert.That(rpcTx, Is.TypeOf<LegacyTransactionForRpc>());
        LegacyTransactionForRpc legacyRpcTx = (LegacyTransactionForRpc)rpcTx;

        Assert.That(legacyRpcTx.ChainId, Is.EqualTo(explicitChainId));
    }
}
