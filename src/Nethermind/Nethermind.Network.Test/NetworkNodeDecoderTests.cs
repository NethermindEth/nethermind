// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
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
            NetworkNodeDecoder networkNodeDecoder = new();
            NetworkNode node = new(TestItem.PublicKeyA, "127.0.0.1", 30303, 100L);
            Rlp encoded = networkNodeDecoder.Encode(node);
            NetworkNode decoded = networkNodeDecoder.Decode(encoded.Bytes.AsRlpStream());
            Assert.That(decoded.Host, Is.EqualTo(node.Host));
            Assert.That(decoded.NodeId, Is.EqualTo(node.NodeId));
            Assert.That(decoded.Port, Is.EqualTo(node.Port));
            Assert.That(decoded.Reputation, Is.EqualTo(node.Reputation));
        }

        [Test]
        public void Can_do_roundtrip_negative_reputation()
        {
            NetworkNodeDecoder networkNodeDecoder = new();
            NetworkNode node = new(TestItem.PublicKeyA, "127.0.0.1", 30303, -100L);
            Rlp encoded = networkNodeDecoder.Encode(node);
            NetworkNode decoded = networkNodeDecoder.Decode(encoded.Bytes.AsRlpStream());
            Assert.That(decoded.Host, Is.EqualTo(node.Host));
            Assert.That(decoded.NodeId, Is.EqualTo(node.NodeId));
            Assert.That(decoded.Port, Is.EqualTo(node.Port));
            Assert.That(decoded.Reputation, Is.EqualTo(node.Reputation));
        }

        [Test]
        public void Can_read_regression()
        {
            NetworkNodeDecoder networkNodeDecoder = new();
            Rlp encoded = new(Bytes.FromHexString("f8a7b84013a1107b6f78a4977222d2d5a4cd05a8a042b75222c8ec99129b83793eda3d214208d4e835617512fc8d148d3d1b4d89530861644f531675b1fb64b785c6c152953a3a666666663a38352e3131322e3131332e3138368294c680ce0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"));
            NetworkNode decoded = networkNodeDecoder.Decode(encoded.Bytes.AsRlpStream());
            Assert.That(decoded.Host, Is.EqualTo("::ffff:85.112.113.186"));
            Assert.That(decoded.NodeId, Is.EqualTo(new PublicKey(Bytes.FromHexString("0x13a1107b6f78a4977222d2d5a4cd05a8a042b75222c8ec99129b83793eda3d214208d4e835617512fc8d148d3d1b4d89530861644f531675b1fb64b785c6c152"))));
            Assert.That(decoded.Port, Is.EqualTo(38086));
            Assert.That(decoded.Reputation, Is.EqualTo(0L));
        }

        [Test]
        public void Negative_port_just_in_case_for_resilience()
        {
            NetworkNodeDecoder networkNodeDecoder = new();
            NetworkNode node = new(TestItem.PublicKeyA, "127.0.0.1", -1, -100L);
            Rlp encoded = networkNodeDecoder.Encode(node);
            NetworkNode decoded = networkNodeDecoder.Decode(encoded.Bytes.AsRlpStream());
            Assert.That(decoded.Host, Is.EqualTo(node.Host));
            Assert.That(decoded.NodeId, Is.EqualTo(node.NodeId));
            Assert.That(decoded.Port, Is.EqualTo(node.Port));
            Assert.That(decoded.Reputation, Is.EqualTo(node.Reputation));
        }
    }
}
