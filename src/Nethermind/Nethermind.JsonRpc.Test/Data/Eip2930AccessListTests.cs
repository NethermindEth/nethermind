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
    public class Eip2930AccessListTests
    {
        private readonly EthereumJsonSerializer _serializer = new EthereumJsonSerializer();
        private Transaction _transaction = new Transaction();
        private Dictionary<Address, IReadOnlySet<UInt256>> _data = new Dictionary<Address, IReadOnlySet<UInt256>>();
        private TransactionForRpc _transactionForRpc = new TransactionForRpc();
        private HashSet<UInt256> _storageKeys = new HashSet<UInt256>();


        [Test]
        public void can_serialize_valid_accessList()
        {
            _data = new Dictionary<Address, IReadOnlySet<UInt256>>();
            _transaction = new Transaction();
            _transaction.Type = TxType.AccessList;

            _storageKeys = new HashSet<UInt256> {1, 2, 3, 5, 8};
            _data.Add(TestItem.AddressA, _storageKeys);
            _storageKeys = new HashSet<UInt256> {42};
            _data.Add(TestItem.AddressB, _storageKeys);
            _transaction.AccessList = new AccessList(_data);
            _transactionForRpc = new TransactionForRpc(_transaction);

            string serialized = _serializer.Serialize(_transactionForRpc);
            string expected = "{\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"to\":null,\"value\":\"0x0\",\"gasPrice\":\"0x0\",\"gas\":\"0x0\",\"input\":null,\"type\":\"0x1\",\"accessList\":[{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"storageKeys\":[\"0x1\",\"0x2\",\"0x3\",\"0x5\",\"0x8\"]},{\"address\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"storageKeys\":[\"0x2a\"]}]}";
            expected.Should().Be(serialized);
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
        public void can_serialize_accessList_with_null_storageKeys()
        {
            _data = new Dictionary<Address, IReadOnlySet<UInt256>>();
            _transaction = new Transaction();
            _transaction.Type = TxType.AccessList;

            _data.Add(TestItem.AddressA, null);
            _transaction.AccessList = new AccessList(_data);
            _transactionForRpc = new TransactionForRpc(_transaction);

            string serialized = _serializer.Serialize(_transactionForRpc);
            string expected = "{\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"to\":null,\"value\":\"0x0\",\"gasPrice\":\"0x0\",\"gas\":\"0x0\",\"input\":null,\"type\":\"0x1\",\"accessList\":[{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\"}]}";
            expected.Should().Be(serialized);
        }
    }
}
