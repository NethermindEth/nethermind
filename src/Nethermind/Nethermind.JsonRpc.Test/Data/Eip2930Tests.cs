// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using FluentAssertions.Json;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Serialization.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Data;

public class Eip2930Tests
{
    private readonly EthereumJsonSerializer _serializer = new();

    private AccessList GetTestAccessList()
    {
        return new AccessList.Builder()
            .AddAddress(TestItem.AddressA)
            .AddStorage(1)
            .AddStorage(2)
            .AddStorage(3)
            .AddStorage(5)
            .AddStorage(8)
            .AddAddress(TestItem.AddressB)
            .AddStorage(42)
            .Build();
    }

    [TestCase(TxType.AccessList, """{"nonce":"0x0","blockHash":null,"blockNumber":null,"transactionIndex":null,"to":null,"value":"0x0","gasPrice":"0x0","gas":"0x0","input":null,"type":"0x1","chainId":"0x1","accessList":[{"address":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","storageKeys":["0x0000000000000000000000000000000000000000000000000000000000000001","0x0000000000000000000000000000000000000000000000000000000000000002","0x0000000000000000000000000000000000000000000000000000000000000003","0x0000000000000000000000000000000000000000000000000000000000000005","0x0000000000000000000000000000000000000000000000000000000000000008"]},{"address":"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358","storageKeys":["0x000000000000000000000000000000000000000000000000000000000000002a"]}]}""")]
    [TestCase(TxType.EIP1559, """{"nonce":"0x0","blockHash":null,"blockNumber":null,"transactionIndex":null,"to":null,"value":"0x0","gasPrice":"0x0","maxPriorityFeePerGas":"0x0","maxFeePerGas":"0x0","gas":"0x0","input":null,"type":"0x2","chainId":"0x1","accessList":[{"address":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","storageKeys":["0x0000000000000000000000000000000000000000000000000000000000000001","0x0000000000000000000000000000000000000000000000000000000000000002","0x0000000000000000000000000000000000000000000000000000000000000003","0x0000000000000000000000000000000000000000000000000000000000000005","0x0000000000000000000000000000000000000000000000000000000000000008"]},{"address":"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358","storageKeys":["0x000000000000000000000000000000000000000000000000000000000000002a"]}]}""")]
    public void can_serialize_valid_accessList(TxType txType, string txJson)
    {
        Transaction transaction = new()
        {
            Type = txType,
            AccessList = GetTestAccessList(),
        };
        TransactionForRpc transactionForRpc = TransactionForRpc.FromTransaction(transaction);
        string serialized = _serializer.Serialize(transactionForRpc);

        var actual = JObject.Parse(serialized).Property("accessList");
        var expected = JObject.Parse(txJson).Property("accessList");
        actual.Should().BeEquivalentTo(expected);
    }

    [TestCase(TxType.AccessList, """{"nonce":"0x0","blockHash":null,"blockNumber":null,"transactionIndex":null,"to":null,"value":"0x0","gasPrice":"0x0","gas":"0x0","input":null,"type":"0x01","chainId":"0x01","accessList":[{"address":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","storageKeys":["0x1","0x2","0x3","0x5","0x8"]},{"address":"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358","storageKeys":["0x2a"]}]}""")]
    [TestCase(TxType.EIP1559, """{"nonce":"0x0","blockHash":null,"blockNumber":null,"transactionIndex":null,"to":null,"value":"0x0","gasPrice":"0x0","maxFeePerGas":"0x0","maxPriorityFeePerGas":"0x0","gas":"0x0","input":null,"type":"0x02","chainId":"0x01","accessList":[{"address":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","storageKeys":["0x1","0x2","0x3","0x5","0x8"]},{"address":"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358","storageKeys":["0x2a"]}]}""")]
    public void can_deserialize_valid_accessList(TxType txType, string txJson)
    {
        Transaction transaction = new()
        {
            Type = txType,
            AccessList = GetTestAccessList(),
        };
        TransactionForRpc transactionForRpc = TransactionForRpc.FromTransaction(transaction);

        TransactionForRpc deserializedTxForRpc = _serializer.Deserialize<TransactionForRpc>(txJson);

        deserializedTxForRpc.Should().BeEquivalentTo(transactionForRpc);
    }

    [TestCase(TxType.Legacy)]
    public void can_serialize_null_accessList_to_nothing(TxType txType)
    {
        Transaction transaction = new()
        {
            Type = txType,
        };
        var rpc = TransactionForRpc.FromTransaction(transaction);

        string serialized = _serializer.Serialize(rpc);

        JObject.Parse(serialized).Should().NotHaveElement("accessList");
    }

    [TestCase(TxType.AccessList)]
    [TestCase(TxType.EIP1559)]
    public void can_serialize_null_accessList_to_empty_array(TxType txType)
    {
        Transaction transaction = new()
        {
            Type = txType,
        };
        var rpc = TransactionForRpc.FromTransaction(transaction);

        string serialized = _serializer.Serialize(rpc);

        JObject.Parse(serialized).GetValue("accessList").Should().BeEquivalentTo(new JArray());
    }

    [Test]
    public void can_deserialize_null_accessList()
    {
        string json = """{"nonce":"0x0","blockHash":null,"blockNumber":null,"transactionIndex":null,"to":null,"value":"0x0","gasPrice":"0x0","gas":"0x0","input":null,"accessList":null}""";

        var transactionForRpc = _serializer.Deserialize<TransactionForRpc>(json);

        transactionForRpc.Type.Should().Be(TxType.AccessList);
        ((AccessListTransactionForRpc)transactionForRpc).AccessList.Should().BeNull();
    }

    [Test]
    public void can_deserialize_no_accessList()
    {
        string json = """{"nonce":"0x0","blockHash":null,"blockNumber":null,"transactionIndex":null,"to":null,"value":"0x0","gasPrice":"0x0","gas":"0x0","input":null}""";

        TransactionForRpc transactionForRpc = _serializer.Deserialize<TransactionForRpc>(json);
        var transaction = transactionForRpc.ToTransaction();

        transaction.Type.Should().Be(TxType.Legacy);
        transaction.AccessList.Should().BeNull();
    }

    [TestCase(TxType.AccessList, """{"nonce":"0x0","blockHash":null,"blockNumber":null,"transactionIndex":null,"to":null,"value":"0x0","gasPrice":"0x0","gas":"0x0","input":null,"type":"0x1","chainId":"0x1","accessList":[]}""")]
    [TestCase(TxType.EIP1559, """{"nonce":"0x0","blockHash":null,"blockNumber":null,"transactionIndex":null,"to":null,"value":"0x0","gasPrice":"0x0","maxPriorityFeePerGas":"0x0","maxFeePerGas":"0x0","gas":"0x0","input":null,"type":"0x2","chainId":"0x1","accessList":[]}""")]
    public void can_serialize_empty_accessList(TxType txType, string txJson)
    {
        Transaction transaction = new()
        {
            Type = txType,
            AccessList = AccessList.Empty,
        };
        var transactionForRpc = TransactionForRpc.FromTransaction(transaction);
        string serialized = _serializer.Serialize(transactionForRpc);

        var actual = JObject.Parse(serialized).Property("accessList");
        var expected = JObject.Parse(txJson).Property("accessList");
        actual.Should().BeEquivalentTo(expected);
    }

    [Test]
    public void can_deserialize_empty_accessList()
    {
        string json = """{"nonce":"0x0","blockHash":null,"blockNumber":null,"transactionIndex":null,"to":null,"value":"0x0","gasPrice":"0x0","gas":"0x0","input":null,"accessList":[]}""";
        TransactionForRpc transactionForRpc = _serializer.Deserialize<TransactionForRpc>(json);

        transactionForRpc.Type.Should().Be(TxType.AccessList);

        var expected = AccessList.Empty;
        var actual = ((AccessListTransactionForRpc)transactionForRpc).AccessList!.ToAccessList();
        actual.Should().BeEquivalentTo(expected);
    }

    [TestCase(TxType.AccessList, """{"nonce":"0x0","blockHash":null,"blockNumber":null,"transactionIndex":null,"to":null,"value":"0x0","gasPrice":"0x0","gas":"0x0","input":null,"type":"0x1","chainId":"0x1","accessList":[{"address":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","storageKeys":[]}]}""")]
    [TestCase(TxType.EIP1559, """{"nonce":"0x0","blockHash":null,"blockNumber":null,"transactionIndex":null,"to":null,"value":"0x0","gasPrice":"0x0","maxPriorityFeePerGas":"0x0","maxFeePerGas":"0x0","gas":"0x0","input":null,"type":"0x2","chainId":"0x1","accessList":[{"address":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","storageKeys":[]}]}""")]
    public void can_serialize_accessList_with_empty_storageKeys(TxType txType, string txJson)
    {
        AccessList accessList = new AccessList.Builder()
            .AddAddress(TestItem.AddressA)
            .Build();
        Transaction transaction = new()
        {
            Type = txType,
            AccessList = accessList,
        };

        var transactionForRpc = TransactionForRpc.FromTransaction(transaction);
        string serialized = _serializer.Serialize(transactionForRpc);

        var actual = JObject.Parse(serialized).Property("accessList");
        var expected = JObject.Parse(txJson).Property("accessList");
        actual.Should().BeEquivalentTo(expected);
    }

    [Test]
    public void can_deserialize_accessList_with_empty_storageKeys()
    {
        string json = """{"nonce":"0x0","blockHash":null,"blockNumber":null,"transactionIndex":null,"to":null,"value":"0x0","gasPrice":"0x0","gas":"0x0","input":null,"accessList":[{"address":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","storageKeys":[]}]}""";
        TransactionForRpc transactionForRpc = _serializer.Deserialize<TransactionForRpc>(json);

        transactionForRpc.Type.Should().Be(TxType.AccessList);

        AccessList expected = new AccessList.Builder().AddAddress(TestItem.AddressA).Build();
        AccessList actual = ((AccessListTransactionForRpc)transactionForRpc).AccessList!.ToAccessList();
        expected.Should().BeEquivalentTo(actual);
    }

    [Test]
    public void can_deserialize_accessList_with_null_storageKeys()
    {
        string json = """{"nonce":"0x0","blockHash":null,"blockNumber":null,"transactionIndex":null,"to":null,"value":"0x0","gasPrice":"0x0","gas":"0x0","input":null,"accessList":[{"address":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099"}]}""";
        TransactionForRpc transactionForRpc = _serializer.Deserialize<TransactionForRpc>(json);

        transactionForRpc.Type.Should().Be(TxType.AccessList);

        AccessList expected = new AccessList.Builder().AddAddress(TestItem.AddressA).Build();
        AccessList actual = ((AccessListTransactionForRpc)transactionForRpc).AccessList!.ToAccessList();
        expected.Should().BeEquivalentTo(actual);
    }

    [Test]
    public void can_deserialize_accessList_with_null_storageKeys_and_eip1559_txType()
    {
        string json = """{"nonce":"0x0","blockHash":null,"blockNumber":null,"transactionIndex":null,"to":null,"value":"0x0","maxFeePerGas":"0x10","gas":"0x0","input":null,"accessList":[{"address":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099"}]}""";
        TransactionForRpc transactionForRpc = _serializer.Deserialize<TransactionForRpc>(json);

        transactionForRpc.Type.Should().Be(TxType.EIP1559);

        AccessList expected = new AccessList.Builder().AddAddress(TestItem.AddressA).Build();
        AccessList actual = ((EIP1559TransactionForRpc)transactionForRpc).AccessList!.ToAccessList();
        expected.Should().BeEquivalentTo(actual);
    }

    [Test]
    public void can_deserialize_not_provided_txType()
    {
        string json = """{"nonce":"0x0","blockHash":null,"blockNumber":null,"transactionIndex":null,"to":null,"value":"0x0","gasPrice":"0x0","gas":"0x0","input":null}""";

        TransactionForRpc transactionForRpc = _serializer.Deserialize<TransactionForRpc>(json);

        // If there is not `TxType` provided, default value should be `TxType.Legacy`
        transactionForRpc.Type.Should().Be(TxType.Legacy);
    }

    [TestCase(TxType.AccessList)]
    [TestCase(TxType.EIP1559)]
    public void can_convert_fromTransaction_toTransactionForRpc_and_back(TxType txType)
    {
        Transaction transaction = new()
        {
            Type = txType,
            AccessList = GetTestAccessList(),
            ChainId = BlockchainIds.Mainnet,
            SenderAddress = Address.SystemUser,
            Data = Memory<byte>.Empty,
        };
        TransactionForRpc transactionForRpc = TransactionForRpc.FromTransaction(transaction);

        Transaction afterConversion = transactionForRpc.ToTransaction();

        afterConversion.Should().BeEquivalentTo(transaction, static option => option.ComparingByMembers<Transaction>().Excluding(static tx => tx.Data));
        afterConversion.Data.AsArray().Should().BeEquivalentTo(transaction.Data.AsArray());
    }
}
