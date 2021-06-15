//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Data
{
    public class Eip2930Tests
    {
        private readonly EthereumJsonSerializer _serializer = new EthereumJsonSerializer();
        private Transaction _transaction = new Transaction();
        private Dictionary<Address, IReadOnlySet<UInt256>> _data = new Dictionary<Address, IReadOnlySet<UInt256>>();
        private TransactionForRpc _transactionForRpc = new TransactionForRpc();
        private HashSet<UInt256> _storageKeys = new HashSet<UInt256>();

        private AccessList GetTestAccessList()
        {
            AccessListBuilder accessListBuilder = new AccessListBuilder();

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

        [Test]
        public void can_serialize_valid_accessList()
        {
            _transaction = new Transaction();
            _transaction.Type = TxType.AccessList;
            _transaction.AccessList = GetTestAccessList();
            _transactionForRpc = new TransactionForRpc(_transaction);

            string serialized = _serializer.Serialize(_transactionForRpc);
            string expected = "{\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"to\":null,\"value\":\"0x0\",\"gasPrice\":\"0x0\",\"gas\":\"0x0\",\"input\":null,\"type\":\"0x1\",\"accessList\":[{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"storageKeys\":[\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"0x0000000000000000000000000000000000000000000000000000000000000002\",\"0x0000000000000000000000000000000000000000000000000000000000000003\",\"0x0000000000000000000000000000000000000000000000000000000000000005\",\"0x0000000000000000000000000000000000000000000000000000000000000008\"]},{\"address\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"storageKeys\":[\"0x000000000000000000000000000000000000000000000000000000000000002a\"]}]}";
            expected.Should().Be(serialized);
        }

        [Test]
        public void can_deserialize_valid_accessList()
        {
            TransactionForRpc deserializedTxForRpc = _serializer.Deserialize<TransactionForRpc>("{\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"to\":null,\"value\":\"0x0\",\"gasPrice\":\"0x0\",\"gas\":\"0x0\",\"input\":null,\"type\":\"0x01\",\"accessList\":[{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"storageKeys\":[\"0x1\",\"0x2\",\"0x3\",\"0x5\",\"0x8\"]},{\"address\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"storageKeys\":[\"0x2a\"]}]}");
            
            _transaction = new Transaction();
            _transaction.Type = TxType.AccessList;
            _transaction.AccessList = GetTestAccessList();
            _transactionForRpc = new TransactionForRpc(_transaction);
            
            deserializedTxForRpc.Should().BeEquivalentTo(_transactionForRpc);
        }

        [Test]
        public void can_serialize_null_accessList()
        {
            _transaction = new Transaction();
            _transaction.Type = TxType.AccessList;
            _transactionForRpc = new TransactionForRpc(_transaction);

            string serialized = _serializer.Serialize(_transactionForRpc);
            string expected = "{\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"to\":null,\"value\":\"0x0\",\"gasPrice\":\"0x0\",\"gas\":\"0x0\",\"input\":null,\"type\":\"0x1\"}";
            expected.Should().Be(serialized);
        }

        [Test]
        public void can_deserialize_null_accessList()
        {
            _transactionForRpc = _serializer.Deserialize<TransactionForRpc>("{\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"to\":null,\"value\":\"0x0\",\"gasPrice\":\"0x0\",\"gas\":\"0x0\",\"input\":null,\"type\":\"0x01\"}");

            _transactionForRpc.Type.Should().Be(1);
            _transactionForRpc.AccessList.Should().BeNull();
        }

        [Test]
        public void can_serialize_empty_accessList()
        {
            _data = new Dictionary<Address, IReadOnlySet<UInt256>>();
            _transaction = new Transaction();
            _transaction.Type = TxType.AccessList;
            _transaction.AccessList = new AccessList(_data);
            _transactionForRpc = new TransactionForRpc(_transaction);

            string serialized = _serializer.Serialize(_transactionForRpc);
            string expected = "{\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"to\":null,\"value\":\"0x0\",\"gasPrice\":\"0x0\",\"gas\":\"0x0\",\"input\":null,\"type\":\"0x1\",\"accessList\":[]}";
            expected.Should().Be(serialized);
        }

        [Test]
        public void can_deserialize_empty_accessList()
        {
            _transactionForRpc = _serializer.Deserialize<TransactionForRpc>("{\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"to\":null,\"value\":\"0x0\",\"gasPrice\":\"0x0\",\"gas\":\"0x0\",\"input\":null,\"type\":\"0x01\",\"accessList\":[]}");

            _transactionForRpc.Type.Should().Be(1);
            _transactionForRpc.AccessList.Length.Should().Be(0);
        }

        [Test]
        public void can_serialize_accessList_with_empty_storageKeys()
        {
            _data = new Dictionary<Address, IReadOnlySet<UInt256>>();
            _transaction = new Transaction();
            _transaction.Type = TxType.AccessList;

            _storageKeys = new HashSet<UInt256> {};
            _data.Add(TestItem.AddressA, _storageKeys);
            _transaction.AccessList = new AccessList(_data);
            _transactionForRpc = new TransactionForRpc(_transaction);

            string serialized = _serializer.Serialize(_transactionForRpc);
            string expected = "{\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"to\":null,\"value\":\"0x0\",\"gasPrice\":\"0x0\",\"gas\":\"0x0\",\"input\":null,\"type\":\"0x1\",\"accessList\":[{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"storageKeys\":[]}]}";
            expected.Should().Be(serialized);
        }

        [Test]
        public void can_deserialize_accessList_with_empty_storageKeys()
        {
            _transactionForRpc = _serializer.Deserialize<TransactionForRpc>("{\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"to\":null,\"value\":\"0x0\",\"gasPrice\":\"0x0\",\"gas\":\"0x0\",\"input\":null,\"type\":\"0x01\",\"accessList\":[{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"storageKeys\":[]}]}");

            object[] accessList = {new AccessListItemForRpc(TestItem.AddressA, new HashSet<UInt256>{})};
            _transactionForRpc.Type.Should().Be(1);
            _transactionForRpc.AccessList.Should().BeEquivalentTo(accessList);
        }
        
        [Test]
        public void can_serialize_accessList_with_null_storageKeys()
        {
            _data = new Dictionary<Address, IReadOnlySet<UInt256>>();
            _transaction = new Transaction();
            _transaction.Type = TxType.AccessList;

            _data.Add(TestItem.AddressA, null);
            _transaction.AccessList = new AccessList(_data);
            _transactionForRpc = new TransactionForRpc(_transaction);

            string serialized = _serializer.Serialize(_transactionForRpc);
            string expected = "{\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"to\":null,\"value\":\"0x0\",\"gasPrice\":\"0x0\",\"gas\":\"0x0\",\"input\":null,\"type\":\"0x1\",\"accessList\":[{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"storageKeys\":[]}]}";
            expected.Should().Be(serialized);
        }

        [Test]
        public void can_deserialize_accessList_with_null_storageKeys()
        {
            _transactionForRpc = _serializer.Deserialize<TransactionForRpc>("{\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"to\":null,\"value\":\"0x0\",\"gasPrice\":\"0x0\",\"gas\":\"0x0\",\"input\":null,\"type\":\"0x01\",\"accessList\":[{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\"}]}");
            
            object[] accessList = {new AccessListItemForRpc(TestItem.AddressA, null)};
            _transactionForRpc.Type.Should().Be(1);
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

        [Test]
        public void can_convert_from_Transaction_to_TransactionForRpc_and_back()
        {
            _transaction = new Transaction();
            _transaction.Type = TxType.AccessList;

            _transaction.AccessList = GetTestAccessList();
            _transactionForRpc = new TransactionForRpc(_transaction);
            Transaction afterConversion = _transactionForRpc.ToTransaction();

            afterConversion.Should().BeEquivalentTo(_transaction);
        }
    }
}
