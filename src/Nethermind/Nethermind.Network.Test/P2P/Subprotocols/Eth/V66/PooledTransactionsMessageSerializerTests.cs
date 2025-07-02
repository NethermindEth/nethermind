// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Collections;
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
                Data = System.Array.Empty<byte>(),
                Signature = new Signature(
                    new Hash256("0x64b1702d9298fee62dfeccc57d322a463ad55ca201256d01f62b45b2e1c21c12").Bytes,
                    new Hash256("0x64b1702d9298fee62dfeccc57d322a463ad55ca201256d01f62b45b2e1c21c10").Bytes, 0x25),
                Hash = new Hash256("0x588df025c4c2d757d3e314bd3dfbfe352687324e6b8557ad1731585e96928aed")
            };

            Transaction tx2 = new()
            {
                Nonce = 9,
                GasPrice = 0x4a817c809,
                GasLimit = 0x33450,
                To = new Address("0x3535353535353535353535353535353535353535"),
                Value = 0x2d9,
                Data = System.Array.Empty<byte>(),
                Signature = new Signature(
                    new Hash256("0x52f8f61201b2b11a78d6e866abc9c3db2ae8631fa656bfe5cb53668255367afb").Bytes,
                    new Hash256("0x52f8f61201b2b11a78d6e866abc9c3db2ae8631fa656bfe5cb53668255367afb").Bytes, 0x25),
                Hash = new Hash256("0xf39c7dac06a9f3abf09faf5e30439a349d3717611b3ed337cd52b0d192bc72da")
            };

            using var ethMessage = new Network.P2P.Subprotocols.Eth.V65.Messages.PooledTransactionsMessage(new ArrayPoolList<Transaction>(2) { tx1, tx2 });

            PooledTransactionsMessage message = new(1111, ethMessage);

            PooledTransactionsMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, message, "f8d7820457f8d2f867088504a817c8088302e2489435353535353535353535353535353535353535358202008025a064b1702d9298fee62dfeccc57d322a463ad55ca201256d01f62b45b2e1c21c12a064b1702d9298fee62dfeccc57d322a463ad55ca201256d01f62b45b2e1c21c10f867098504a817c809830334509435353535353535353535353535353535353535358202d98025a052f8f61201b2b11a78d6e866abc9c3db2ae8631fa656bfe5cb53668255367afba052f8f61201b2b11a78d6e866abc9c3db2ae8631fa656bfe5cb53668255367afb");
        }

        // Generated during Hive devp2p
        [TestCase(
            "f8f78808a6f328496c6b92f8ecb87403f871f86c870c72dd9d5e883e800107830186a09400000000000000000000000000000000000000008080c001c001a00e0f68ba5a5f7820c9b47f4388b542f55849114944b6ac20d9f14cc2e3aa0368a00ad549ecdcc4756e5f05f6364ded164a1b7d69424d232ea8f29c35420efb39c4c0c0c0b87403f871f86c870c72dd9d5e883e010107830186a09400000000000000000000000000000000000000008080c001c080a0cd2109f56cec3c639c72e2d407e85eb2e54a2a6c4cd94cafe251b62ad1dbb723a068e1fee4a81ffccb9720b8a342f2cb6278570917132e9f639339b778f2da7169c0c0c0"
        )]
        public void Deserialize(string hex)
        {
            PooledTransactionsMessageSerializer serializer = new();

            serializer.Deserialize(Convert.FromHexString(hex));
        }
    }
}
