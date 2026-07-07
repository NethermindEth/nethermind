// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Network.Enr;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
using System.Net;

namespace Nethermind.Network.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class NetworkNodeDecoderTests
    {
        [TestCase("127.0.0.1", 30303, 100L)]
        [TestCase("127.0.0.1", 30303, -100L)]
        [TestCase("127.0.0.1", -1, -100L)]
        public void Can_do_roundtrip(string host, int port, long reputation)
        {
            NetworkNode node = new(TestItem.PublicKeyA, host, port, reputation);
            AssertRoundtripPreservesFields(node);
        }

        [Test]
        public void Can_read_regression()
        {
            NetworkNodeDecoder networkNodeDecoder = new();
            Rlp encoded = new(Bytes.FromHexString("f8a7b84013a1107b6f78a4977222d2d5a4cd05a8a042b75222c8ec99129b83793eda3d214208d4e835617512fc8d148d3d1b4d89530861644f531675b1fb64b785c6c152953a3a666666663a38352e3131322e3131332e3138368294c680ce0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"));
            RlpReader context = new(encoded.Bytes);
            NetworkNode decoded = networkNodeDecoder.Decode(ref context);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(decoded.Host, Is.EqualTo("::ffff:85.112.113.186"));
                Assert.That(decoded.NodeId, Is.EqualTo(new PublicKey(Bytes.FromHexString("0x13a1107b6f78a4977222d2d5a4cd05a8a042b75222c8ec99129b83793eda3d214208d4e835617512fc8d148d3d1b4d89530861644f531675b1fb64b785c6c152"))));
                Assert.That(decoded.Port, Is.EqualTo(38086));
                Assert.That(decoded.Reputation, Is.EqualTo(0L));
            }
        }

        private static void AssertRoundtripPreservesFields(NetworkNode node)
        {
            NetworkNodeDecoder networkNodeDecoder = new();
            Rlp encoded = networkNodeDecoder.Encode(node);
            RlpReader context = new(encoded.Bytes);
            NetworkNode decoded = networkNodeDecoder.Decode(ref context);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(decoded.Host, Is.EqualTo(node.Host));
                Assert.That(decoded.NodeId, Is.EqualTo(node.NodeId));
                Assert.That(decoded.Port, Is.EqualTo(node.Port));
                Assert.That(decoded.Reputation, Is.EqualTo(node.Reputation));
            }
        }

        [Test]
        public void Can_do_enr_roundtrip()
        {
            NetworkNodeDecoder networkNodeDecoder = new();
            NodeRecord enr = CreateTestEnr(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"), 30303, 30304);
            NetworkNode node = new(enr.ToString())
            {
                Reputation = 100L
            };

            Rlp encoded = networkNodeDecoder.Encode(node);
            RlpReader context = new(encoded.Bytes);
            NetworkNode decoded = networkNodeDecoder.Decode(ref context);

            using (Assert.EnterMultipleScope())
            {
                NodeRecord? decodedEnr = decoded.Enr;
                Assert.That(decoded.IsEnr, Is.True);
                Assert.That(decodedEnr, Is.Not.Null);
                Assert.That(decodedEnr!.ToString(), Is.EqualTo(enr.ToString()));
                Assert.That(decoded.NodeId, Is.EqualTo(node.NodeId));
                Assert.That(decoded.Host, Is.EqualTo("8.8.8.8"));
                Assert.That(decoded.Port, Is.EqualTo(30303));
                Assert.That(decoded.Reputation, Is.EqualTo(node.Reputation));
            }
        }

        private static NodeRecord CreateTestEnr(PrivateKey privateKey, IPAddress ipAddress, int tcpPort, int udpPort)
        {
            NodeRecord enr = new();
            enr.SetEntry(IdEntry.Instance);
            enr.SetEntry(new IpEntry(ipAddress));
            enr.SetEntry(new SecP256k1Entry(privateKey.CompressedPublicKey));
            enr.SetEntry(new TcpEntry(tcpPort));
            enr.SetEntry(new UdpEntry(udpPort));
            enr.EnrSequence = 1;
            new NodeRecordSigner(new EthereumEcdsa(0), privateKey).Sign(enr);

            return enr;
        }
    }
}
