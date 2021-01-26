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

using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class NetworkNodeDecoderTests
    {
        [Test]
        public void Can_do_roundtrip()
        {
            NetworkNodeDecoder networkNodeDecoder = new NetworkNodeDecoder();
            NetworkNode node = new NetworkNode(TestItem.PublicKeyA, "127.0.0.1", 30303, 100L);
            Rlp encoded = networkNodeDecoder.Encode(node);
            NetworkNode decoded = networkNodeDecoder.Decode(encoded.Bytes.AsRlpStream());
            Assert.AreEqual(node.Host, decoded.Host);
            Assert.AreEqual(node.NodeId, decoded.NodeId);
            Assert.AreEqual(node.Port, decoded.Port);
            Assert.AreEqual(node.Reputation, decoded.Reputation);
        }
        
        [Test]
        public void Can_do_roundtrip_negative_reputation()
        {
            NetworkNodeDecoder networkNodeDecoder = new NetworkNodeDecoder();
            NetworkNode node = new NetworkNode(TestItem.PublicKeyA, "127.0.0.1", 30303, -100L);
            Rlp encoded = networkNodeDecoder.Encode(node);
            NetworkNode decoded = networkNodeDecoder.Decode(encoded.Bytes.AsRlpStream());
            Assert.AreEqual(node.Host, decoded.Host);
            Assert.AreEqual(node.NodeId, decoded.NodeId);
            Assert.AreEqual(node.Port, decoded.Port);
            Assert.AreEqual(node.Reputation, decoded.Reputation);
        }
        
        [Test]
        public void Can_read_regression()
        {
            NetworkNodeDecoder networkNodeDecoder = new NetworkNodeDecoder();
            Rlp encoded = new Rlp(Bytes.FromHexString("f8a7b84013a1107b6f78a4977222d2d5a4cd05a8a042b75222c8ec99129b83793eda3d214208d4e835617512fc8d148d3d1b4d89530861644f531675b1fb64b785c6c152953a3a666666663a38352e3131322e3131332e3138368294c680ce0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"));
            NetworkNode decoded = networkNodeDecoder.Decode(encoded.Bytes.AsRlpStream());
            Assert.AreEqual("::ffff:85.112.113.186", decoded.Host);
            Assert.AreEqual(new PublicKey(Bytes.FromHexString("0x13a1107b6f78a4977222d2d5a4cd05a8a042b75222c8ec99129b83793eda3d214208d4e835617512fc8d148d3d1b4d89530861644f531675b1fb64b785c6c152")), decoded.NodeId);
            Assert.AreEqual(38086, decoded.Port);
            Assert.AreEqual(0L, decoded.Reputation);
        }
        
        [Test]
        public void Negative_port_just_in_case_for_resilience()
        {
            NetworkNodeDecoder networkNodeDecoder = new NetworkNodeDecoder();
            NetworkNode node = new NetworkNode(TestItem.PublicKeyA, "127.0.0.1", -1, -100L);
            Rlp encoded = networkNodeDecoder.Encode(node);
            NetworkNode decoded = networkNodeDecoder.Decode(encoded.Bytes.AsRlpStream());
            Assert.AreEqual(node.Host, decoded.Host);
            Assert.AreEqual(node.NodeId, decoded.NodeId);
            Assert.AreEqual(node.Port, decoded.Port);
            Assert.AreEqual(node.Reputation, decoded.Reputation);
        }
    }
}
