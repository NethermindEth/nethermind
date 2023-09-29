// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
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
            Transaction tx1 = new()
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

            Transaction tx2 = new()
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

            var ethMessage = new Network.P2P.Subprotocols.Eth.V65.Messages.PooledTransactionsMessage(new[] { tx1, tx2 });

            PooledTransactionsMessage message = new(1111, ethMessage);

            PooledTransactionsMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, message, "f8d7820457f8d2f867088504a817c8088302e2489435353535353535353535353535353535353535358202008025a064b1702d9298fee62dfeccc57d322a463ad55ca201256d01f62b45b2e1c21c12a064b1702d9298fee62dfeccc57d322a463ad55ca201256d01f62b45b2e1c21c10f867098504a817c809830334509435353535353535353535353535353535353535358202d98025a052f8f61201b2b11a78d6e866abc9c3db2ae8631fa656bfe5cb53668255367afba052f8f61201b2b11a78d6e866abc9c3db2ae8631fa656bfe5cb53668255367afb");
        }
    }
}
