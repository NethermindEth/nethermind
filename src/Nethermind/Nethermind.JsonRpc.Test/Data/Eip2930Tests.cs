// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Data
{
    public class Eip2930Tests
    {
        private readonly EthereumJsonSerializer _serializer = new();
        private Transaction _transaction = new();
        private Dictionary<Address, IReadOnlySet<UInt256>> _data = new();
        private TransactionForRpc _transactionForRpc = new();
        private HashSet<UInt256> _storageKeys = new();

        private AccessList GetTestAccessList()
        {
            AccessListBuilder accessListBuilder = new();

            accessListBuilder.AddAddress(TestItem.AddressA);
            accessListBuilder.AddStorage(1);
            accessListBuilder.AddStorage(2);
            accessListBuilder.AddStorage(3);
            accessListBuilder.AddStorage(5);
            accessListBuilder.AddStorage(8);
            accessListBuilder.AddAddress(TestItem.AddressB);
            accessListBuilder.AddStorage(42);

            return accessListBuilder.ToAccessList();
        }

        [TestCase(TxType.AccessList, "{\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"to\":null,\"value\":\"0x0\",\"gasPrice\":\"0x0\",\"gas\":\"0x0\",\"input\":null,\"type\":\"0x1\",\"accessList\":[{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"storageKeys\":[\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"0x0000000000000000000000000000000000000000000000000000000000000002\",\"0x0000000000000000000000000000000000000000000000000000000000000003\",\"0x0000000000000000000000000000000000000000000000000000000000000005\",\"0x0000000000000000000000000000000000000000000000000000000000000008\"]},{\"address\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"storageKeys\":[\"0x000000000000000000000000000000000000000000000000000000000000002a\"]}]}")]
        [TestCase(TxType.EIP1559, "{\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"to\":null,\"value\":\"0x0\",\"gasPrice\":\"0x0\",\"maxPriorityFeePerGas\":\"0x0\",\"maxFeePerGas\":\"0x0\",\"gas\":\"0x0\",\"input\":null,\"type\":\"0x2\",\"accessList\":[{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"storageKeys\":[\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"0x0000000000000000000000000000000000000000000000000000000000000002\",\"0x0000000000000000000000000000000000000000000000000000000000000003\",\"0x0000000000000000000000000000000000000000000000000000000000000005\",\"0x0000000000000000000000000000000000000000000000000000000000000008\"]},{\"address\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"storageKeys\":[\"0x000000000000000000000000000000000000000000000000000000000000002a\"]}]}")]
        public void can_serialize_valid_accessList(TxType txType, string txJson)
        {
            _transaction = new Transaction();
            _transaction.Type = txType;
            _transaction.AccessList = GetTestAccessList();
            _transactionForRpc = new TransactionForRpc(_transaction);

            string serialized = _serializer.Serialize(_transactionForRpc);
            txJson.Should().Be(serialized);
        }

        [TestCase(TxType.AccessList, "{\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"to\":null,\"value\":\"0x0\",\"gasPrice\":\"0x0\",\"gas\":\"0x0\",\"input\":null,\"type\":\"0x01\",\"accessList\":[{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"storageKeys\":[\"0x1\",\"0x2\",\"0x3\",\"0x5\",\"0x8\"]},{\"address\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"storageKeys\":[\"0x2a\"]}]}")]
        [TestCase(TxType.EIP1559, "{\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"to\":null,\"value\":\"0x0\",\"gasPrice\":\"0x0\",\"maxFeePerGas\":\"0x0\",\"maxPriorityFeePerGas\":\"0x0\",\"gas\":\"0x0\",\"input\":null,\"type\":\"0x02\",\"accessList\":[{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"storageKeys\":[\"0x1\",\"0x2\",\"0x3\",\"0x5\",\"0x8\"]},{\"address\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"storageKeys\":[\"0x2a\"]}]}")]
        public void can_deserialize_valid_accessList(TxType txType, string txJson)
        {
            TransactionForRpc deserializedTxForRpc = _serializer.Deserialize<TransactionForRpc>(txJson);

            _transaction = new Transaction();
            _transaction.Type = txType;
            _transaction.AccessList = GetTestAccessList();
            _transactionForRpc = new TransactionForRpc(_transaction);

            deserializedTxForRpc.Should().BeEquivalentTo(_transactionForRpc);
        }

        [TestCase(TxType.AccessList, "{\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"to\":null,\"value\":\"0x0\",\"gasPrice\":\"0x0\",\"gas\":\"0x0\",\"input\":null,\"type\":\"0x1\"}")]
        [TestCase(TxType.EIP1559, "{\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"to\":null,\"value\":\"0x0\",\"gasPrice\":\"0x0\",\"maxPriorityFeePerGas\":\"0x0\",\"maxFeePerGas\":\"0x0\",\"gas\":\"0x0\",\"input\":null,\"type\":\"0x2\"}")]
        public void can_serialize_null_accessList(TxType txType, string txJson)
        {
            _transaction = new Transaction();
            _transaction.Type = txType;
            _transactionForRpc = new TransactionForRpc(_transaction);

            string serialized = _serializer.Serialize(_transactionForRpc);
            txJson.Should().Be(serialized);
        }

        [Test]
        public void can_deserialize_null_accessList()
        {
            _transactionForRpc = _serializer.Deserialize<TransactionForRpc>("{\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"to\":null,\"value\":\"0x0\",\"gasPrice\":\"0x0\",\"gas\":\"0x0\",\"input\":null,\"type\":\"0x01\"}");

            _transactionForRpc.Type.Should().Be(TxType.AccessList);
            _transactionForRpc.AccessList.Should().BeNull();
        }

        [TestCase(TxType.AccessList, "{\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"to\":null,\"value\":\"0x0\",\"gasPrice\":\"0x0\",\"gas\":\"0x0\",\"input\":null,\"type\":\"0x1\",\"accessList\":[]}")]
        [TestCase(TxType.EIP1559, "{\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"to\":null,\"value\":\"0x0\",\"gasPrice\":\"0x0\",\"maxPriorityFeePerGas\":\"0x0\",\"maxFeePerGas\":\"0x0\",\"gas\":\"0x0\",\"input\":null,\"type\":\"0x2\",\"accessList\":[]}")]
        public void can_serialize_empty_accessList(TxType txType, string txJson)
        {
            _data = new Dictionary<Address, IReadOnlySet<UInt256>>();
            _transaction = new Transaction();
            _transaction.Type = txType;
            _transaction.AccessList = new AccessList(_data);
            _transactionForRpc = new TransactionForRpc(_transaction);

            string serialized = _serializer.Serialize(_transactionForRpc);
            txJson.Should().Be(serialized);
        }

        [Test]
        public void can_deserialize_empty_accessList()
        {
            _transactionForRpc = _serializer.Deserialize<TransactionForRpc>("{\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"to\":null,\"value\":\"0x0\",\"gasPrice\":\"0x0\",\"gas\":\"0x0\",\"input\":null,\"type\":\"0x01\",\"accessList\":[]}")!;

            _transactionForRpc.Type.Should().Be(TxType.AccessList);
            _transactionForRpc.AccessList!.Length.Should().Be(0);
        }

        [TestCase(TxType.AccessList, "{\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"to\":null,\"value\":\"0x0\",\"gasPrice\":\"0x0\",\"gas\":\"0x0\",\"input\":null,\"type\":\"0x1\",\"accessList\":[{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"storageKeys\":[]}]}")]
        [TestCase(TxType.EIP1559, "{\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"to\":null,\"value\":\"0x0\",\"gasPrice\":\"0x0\",\"maxPriorityFeePerGas\":\"0x0\",\"maxFeePerGas\":\"0x0\",\"gas\":\"0x0\",\"input\":null,\"type\":\"0x2\",\"accessList\":[{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"storageKeys\":[]}]}")]
        public void can_serialize_accessList_with_empty_storageKeys(TxType txType, string txJson)
        {
            _data = new Dictionary<Address, IReadOnlySet<UInt256>>();
            _transaction = new Transaction();
            _transaction.Type = txType;

            _storageKeys = new HashSet<UInt256> { };
            _data.Add(TestItem.AddressA, _storageKeys);
            _transaction.AccessList = new AccessList(_data);
            _transactionForRpc = new TransactionForRpc(_transaction);

            string serialized = _serializer.Serialize(_transactionForRpc);
            txJson.Should().Be(serialized);
        }

        [Test]
        public void can_deserialize_accessList_with_empty_storageKeys()
        {
            _transactionForRpc = _serializer.Deserialize<TransactionForRpc>("{\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"to\":null,\"value\":\"0x0\",\"gasPrice\":\"0x0\",\"gas\":\"0x0\",\"input\":null,\"type\":\"0x01\",\"accessList\":[{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"storageKeys\":[]}]}");

            object[] accessList = { new AccessListItemForRpc(TestItem.AddressA, new HashSet<UInt256> { }) };
            _transactionForRpc.Type.Should().Be(TxType.AccessList);
            _transactionForRpc.AccessList.Should().BeEquivalentTo(accessList);
        }

        [TestCase(TxType.AccessList, "{\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"to\":null,\"value\":\"0x0\",\"gasPrice\":\"0x0\",\"gas\":\"0x0\",\"input\":null,\"type\":\"0x1\",\"accessList\":[{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"storageKeys\":[]}]}")]
        [TestCase(TxType.EIP1559, "{\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"to\":null,\"value\":\"0x0\",\"gasPrice\":\"0x0\",\"maxPriorityFeePerGas\":\"0x0\",\"maxFeePerGas\":\"0x0\",\"gas\":\"0x0\",\"input\":null,\"type\":\"0x2\",\"accessList\":[{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"storageKeys\":[]}]}")]
        public void can_serialize_accessList_with_null_storageKeys(TxType txType, string txJson)
        {
            _data = new Dictionary<Address, IReadOnlySet<UInt256>>();
            _transaction = new Transaction();
            _transaction.Type = txType;

            _data.Add(TestItem.AddressA, null);
            _transaction.AccessList = new AccessList(_data);
            _transactionForRpc = new TransactionForRpc(_transaction);

            string serialized = _serializer.Serialize(_transactionForRpc);
            txJson.Should().Be(serialized);
        }

        [Test]
        public void can_deserialize_accessList_with_null_storageKeys()
        {
            _transactionForRpc = _serializer.Deserialize<TransactionForRpc>("{\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"to\":null,\"value\":\"0x0\",\"gasPrice\":\"0x0\",\"gas\":\"0x0\",\"input\":null,\"type\":\"0x01\",\"accessList\":[{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\"}]}");

            object[] accessList = { new AccessListItemForRpc(TestItem.AddressA, null) };
            _transactionForRpc.Type.Should().Be(TxType.AccessList);
            _transactionForRpc.AccessList.Length.Should().Be(1);
            _transactionForRpc.AccessList.Should().BeEquivalentTo(accessList);
        }

        [Test]
        public void can_deserialize_accessList_with_null_storageKeys_and_eip1559_txType()
        {
            _transactionForRpc = _serializer.Deserialize<TransactionForRpc>("{\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"to\":null,\"value\":\"0x0\",\"maxFeePerGas\":\"0x10\",\"gas\":\"0x0\",\"input\":null,\"type\":\"0x02\",\"accessList\":[{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\"}]}");

            object[] accessList = { new AccessListItemForRpc(TestItem.AddressA, null) };
            _transactionForRpc.Type.Should().Be(TxType.EIP1559);
            _transactionForRpc.AccessList.Length.Should().Be(1);
            _transactionForRpc.AccessList.Should().BeEquivalentTo(accessList);
        }

        [Test]
        public void can_deserialize_not_provided_txType()
        {
            _transactionForRpc = _serializer.Deserialize<TransactionForRpc>("{\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"to\":null,\"value\":\"0x0\",\"gasPrice\":\"0x0\",\"gas\":\"0x0\",\"input\":null}");

            // if there is not TxType provided, default value should be TxType.Legacy equal 0
            _transactionForRpc.Type.Should().Be(0);
        }

        [Test]
        public void can_deserialize_direct_null_txType()
        {
            _transactionForRpc = _serializer.Deserialize<TransactionForRpc>("{\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"to\":null,\"value\":\"0x0\",\"gasPrice\":\"0x0\",\"gas\":\"0x0\",\"input\":null,\"type\":null}");

            // if there is null TxType provided, still default value should be TxType.Legacy equal 0
            _transactionForRpc.Type.Should().Be(0);
        }

        [TestCase(TxType.AccessList)]
        [TestCase(TxType.EIP1559)]
        public void can_convert_from_Transaction_to_TransactionForRpc_and_back(TxType txType)
        {
            _transaction = new Transaction();
            _transaction.Type = txType;

            _transaction.AccessList = GetTestAccessList();
            _transactionForRpc = new TransactionForRpc(_transaction);
            Transaction afterConversion = _transactionForRpc.ToTransaction();

            afterConversion.Should().BeEquivalentTo(_transaction, option => option.ComparingByMembers<Transaction>().Excluding(tx => tx.Data));
            afterConversion.Data.FasterToArray().Should().BeEquivalentTo(_transaction.Data.FasterToArray());
        }
    }
}
