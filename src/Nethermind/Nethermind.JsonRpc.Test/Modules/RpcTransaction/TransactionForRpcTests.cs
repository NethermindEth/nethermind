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
    [TestCase("""{"gasPrice":"0x1"}""", typeof(LegacyTransactionForRpc))]
    [TestCase("""{"maxFeePerGas":"0x1","maxPriorityFeePerGas":"0x1"}""", typeof(EIP1559TransactionForRpc))]
    [TestCase("""{"blobVersionedHashes":[]}""", typeof(BlobTransactionForRpc))]
    [TestCase("""{"gasPrice":"0x1","accessList":[]}""", typeof(AccessListTransactionForRpc))]
    [TestCase("""{"gasPrice":"0x1","maxFeePerGas":"0x2","maxPriorityFeePerGas":"0x1"}""", typeof(EIP1559TransactionForRpc))]
    [TestCase("""{"gasPrice":"0x1","blobVersionedHashes":[]}""", typeof(BlobTransactionForRpc))]
    [TestCase("""{"gasPrice":"0x1","authorizationList":[]}""", typeof(SetCodeTransactionForRpc))]
    public void Deserializes_polymorphically_when_declared_as_SignableTransactionForRpc(string json, Type expectedType)
    {
        SignableTransactionForRpc tx = _serializer.Deserialize<SignableTransactionForRpc>(json)
            ?? throw new InvalidOperationException("Expected a deserialized transaction.");

        Assert.That(tx, Is.TypeOf(expectedType),
            "input parameters declared as SignableTransactionForRpc must still dispatch to the concrete tx type");
    }

    [Test]
    public void GasPrice_does_not_drop_the_access_list()
    {
        Transaction tx = ToTransaction(
            """{"gasPrice":"0x7","accessList":[{"address":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","storageKeys":["0x1"]}]}""");

        Assert.That(tx.Type, Is.EqualTo(TxType.AccessList));
        Assert.That(tx.AccessList?.Count, Is.EqualTo((1, 1)));
        Assert.That(tx.GasPrice, Is.EqualTo((UInt256)7));
    }

    [Test]
    public void GasPrice_does_not_drop_the_authorization_list_and_prices_the_dynamic_fee_transaction()
    {
        Transaction tx = ToTransaction(
            """{"gasPrice":"0x7","authorizationList":[{"chainId":"0x1","address":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","nonce":"0x0","yParity":"0x0","r":"0x1","s":"0x1"}]}""");

        Assert.That(tx.Type, Is.EqualTo(TxType.SetCode));
        Assert.That(tx.AuthorizationList?.Length, Is.EqualTo(1));
        Assert.That(tx.MaxFeePerGas, Is.EqualTo((UInt256)7));
        Assert.That(tx.MaxPriorityFeePerGas, Is.EqualTo((UInt256)7));
    }

    [Test]
    public void GasPrice_does_not_drop_the_blob_versioned_hashes()
    {
        Transaction tx = ToTransaction(
            """{"gasPrice":"0x7","to":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","blobVersionedHashes":["0x0100000000000000000000000000000000000000000000000000000000000001"]}""");

        Assert.That(tx.Type, Is.EqualTo(TxType.Blob));
        Assert.That(tx.BlobVersionedHashes?.Length, Is.EqualTo(1));
        Assert.That(tx.MaxFeePerGas, Is.EqualTo((UInt256)7));
    }

    [Test]
    public void Explicit_dynamic_fees_take_precedence_over_gasPrice()
    {
        TransactionForRpc rpcTx = DeserializeTransactionForRpc(
            """{"gasPrice":"0x1","maxFeePerGas":"0x2","maxPriorityFeePerGas":"0x1"}""");

        Transaction tx = rpcTx.ToTransaction().Data!;

        Assert.That(tx.Type, Is.EqualTo(TxType.EIP1559));
        Assert.That(tx.MaxFeePerGas, Is.EqualTo((UInt256)2));
        Assert.That(tx.MaxPriorityFeePerGas, Is.EqualTo((UInt256)1));
        Assert.That(rpcTx.ToTransaction(validateUserInput: true).Error, Is.EqualTo(RpcTransactionErrors.GasPriceInEip1559));
    }

    [TestCase("""{"type":"0x2","to":"0x0000000000000000000000000000000000000001","maxFeePerGas":"0xa","maxPriorityFeePerGas":"0x14"}""", "maxFeePerGas (10) < maxPriorityFeePerGas (20)", TestName = "Fee cap below the priority fee")]
    [TestCase("""{"type":"0x2","maxFeePerGas":"0xa","maxPriorityFeePerGas":"0x14"}""", "maxFeePerGas (10) < maxPriorityFeePerGas (20)", TestName = "Fee cap order is checked before the missing contract data")]
    [TestCase("""{"type":"0x2","to":"0x0000000000000000000000000000000000000001","gasPrice":"0x1","maxFeePerGas":"0xa","maxPriorityFeePerGas":"0x14"}""", RpcTransactionErrors.GasPriceInEip1559, TestName = "Gas price conflict is checked before the fee cap order")]
    [TestCase("""{"type":"0x2","maxFeePerGas":"0x14","maxPriorityFeePerGas":"0xa"}""", RpcTransactionErrors.ContractCreationWithoutData, TestName = "Ordered fees reach the missing contract data check")]
    public void ToValidatedTransaction_rejects_a_fee_cap_below_the_priority_fee_where_it_always_did(string json, string expected) =>
        Assert.That(DeserializeTransactionForRpc(json).ToValidatedTransaction().Error, Is.EqualTo(expected), "first failing check");

    [Test]
    public void ToTransaction_with_input_validation_leaves_the_fee_cap_order_to_the_caller()
    {
        TransactionForRpc rpcTx = DeserializeTransactionForRpc(
            """{"type":"0x2","to":"0x0000000000000000000000000000000000000001","maxFeePerGas":"0xa","maxPriorityFeePerGas":"0x14"}""");

        Result<Transaction> result = rpcTx.ToTransaction(validateUserInput: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsError, Is.False, result.Error);
            Assert.That(result.Data!.MaxFeePerGas, Is.EqualTo((UInt256)10), "fee cap as requested");
        }
    }

    private Transaction ToTransaction(string json)
    {
        TransactionForRpc rpcTx = DeserializeTransactionForRpc(json);
        return rpcTx.ToTransaction().Data!;
    }

    private TransactionForRpc DeserializeTransactionForRpc(string json) =>
        _serializer.Deserialize<TransactionForRpc>(json)
            ?? throw new InvalidOperationException("Expected a deserialized transaction.");

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
