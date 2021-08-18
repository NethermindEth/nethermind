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

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V66
{
    [TestFixture]
    public class PooledTransactionsMessageSerializerTests
    {
        //test from https://github.com/ethereum/EIPs/blob/master/EIPS/eip-2481.md
        [Test]
        public void Roundtrip()
        {
            Transaction tx1 = new Transaction
            {
                Nonce = 8,
                GasPrice = 0x4a817c808,
                GasLimit = 0x2e248,
                To = new Address("0x3535353535353535353535353535353535353535"),
                Value = 0x200,
                Data = new byte[] { },
                Signature = new Signature(
                    new Keccak("0x64b1702d9298fee62dfeccc57d322a463ad55ca201256d01f62b45b2e1c21c12").Bytes,
                    new Keccak("0x64b1702d9298fee62dfeccc57d322a463ad55ca201256d01f62b45b2e1c21c10").Bytes, 0x25),
                Hash = new Keccak("0x588df025c4c2d757d3e314bd3dfbfe352687324e6b8557ad1731585e96928aed")
            };

            Transaction tx2 = new Transaction
            {
                Nonce = 9,
                GasPrice = 0x4a817c809,
                GasLimit = 0x33450,
                To = new Address("0x3535353535353535353535353535353535353535"),
                Value = 0x2d9,
                Data = new byte[] { },
                Signature = new Signature(
                    new Keccak("0x52f8f61201b2b11a78d6e866abc9c3db2ae8631fa656bfe5cb53668255367afb").Bytes,
                    new Keccak("0x52f8f61201b2b11a78d6e866abc9c3db2ae8631fa656bfe5cb53668255367afb").Bytes, 0x25),
                Hash = new Keccak("0xf39c7dac06a9f3abf09faf5e30439a349d3717611b3ed337cd52b0d192bc72da")
            };

            var ethMessage = new Network.P2P.Subprotocols.Eth.V65.PooledTransactionsMessage(new[] {tx1, tx2});

            PooledTransactionsMessage message = new PooledTransactionsMessage(1111, ethMessage);
            
            PooledTransactionsMessageSerializer serializer = new PooledTransactionsMessageSerializer();

            SerializerTester.TestZero(serializer, message, "f8d7820457f8d2f867088504a817c8088302e2489435353535353535353535353535353535353535358202008025a064b1702d9298fee62dfeccc57d322a463ad55ca201256d01f62b45b2e1c21c12a064b1702d9298fee62dfeccc57d322a463ad55ca201256d01f62b45b2e1c21c10f867098504a817c809830334509435353535353535353535353535353535353535358202d98025a052f8f61201b2b11a78d6e866abc9c3db2ae8631fa656bfe5cb53668255367afba052f8f61201b2b11a78d6e866abc9c3db2ae8631fa656bfe5cb53668255367afb");
        }
    }
}
