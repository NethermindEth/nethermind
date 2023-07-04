// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V62
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class StatusMessageSerializerTests
    {
        [Test]
        public void Roundtrip()
        {
            StatusMessage statusMessage = new();
            statusMessage.ProtocolVersion = 63;
            statusMessage.BestHash = Keccak.Compute("1");
            statusMessage.GenesisHash = Keccak.Compute("0");
            statusMessage.TotalDifficulty = 131200;
            statusMessage.NetworkId = 1;

            StatusMessageSerializer serializer = new();
            SerializerTester.TestZero(serializer, statusMessage, "f8483f0183020080a0c89efdaa54c0f20c7adf612882df0950f5a951637e0307cdcb4c672f298b8bc6a0044852b2a670ade5407e78fb2863c51de9fcb96542a07186fe3aeda6bb8a116d");
        }

        [Test]
        public void Roundtrip_empty_status()
        {
            StatusMessage statusMessage = new();
            StatusMessageSerializer serializer = new();
            SerializerTester.TestZero(serializer, statusMessage);
        }

        [Test]
        public void Roundtrip_with_fork_id_next_is_zero()
        {
            StatusMessage statusMessage = new();
            statusMessage.ProtocolVersion = 63;
            statusMessage.BestHash = Keccak.Compute("1");
            statusMessage.GenesisHash = Keccak.Compute("0");
            statusMessage.TotalDifficulty = 131200;
            statusMessage.NetworkId = 1;
            statusMessage.ForkId = new ForkId(12345678, 0);

            StatusMessageSerializer serializer = new();
            SerializerTester.TestZero(serializer, statusMessage);
        }

        [Test]
        public void Roundtrip_with_fork_id_next_is_max()
        {
            StatusMessage statusMessage = new();
            statusMessage.ProtocolVersion = 63;
            statusMessage.BestHash = Keccak.Compute("1");
            statusMessage.GenesisHash = Keccak.Compute("0");
            statusMessage.TotalDifficulty = 131200;
            statusMessage.NetworkId = 1;
            statusMessage.ForkId = new ForkId(12345678, long.MaxValue);

            StatusMessageSerializer serializer = new();
            SerializerTester.TestZero(serializer, statusMessage);
        }

        [TestCase("0x00000000", 0UL, "c6840000000080")]
        [TestCase("0xdeadbeef", null, "ca84deadbeef84baddcafe")]
        [TestCase("0xffffffff", ulong.MaxValue, "ce84ffffffff88ffffffffffffffff")]
        public void Can_serialize_fork_id_properly(string forkHash, ulong? next, string expected)
        {
            next ??= BinaryPrimitives.ReadUInt32BigEndian(Bytes.FromHexString("baddcafe"));
            StatusMessageSerializer serializer = new();
            StatusMessage message = new();
            message.ForkId = new ForkId(Bytes.ReadEthUInt32(Bytes.FromHexString(forkHash)), next.Value);
            serializer.Serialize(message).ToHexString().Should().EndWith(expected);
        }

        [TestCase("f857408314095a8825a025ab40783547a0a161191097c73a00cc6ff0942d3827695241727a5939782d130b0138914211dea070cefc67ff52eb3e1ea9fc9e721d0458a952632927d2d7cb435b250c0c32e653c684e615830180")]
        [TestCase("f84f40058335880ca045a2036c39b7a7ae0113594ba92573c4f9762d874b918238e8aa0bf359abb57ba0bf7e331f7f7c1dd2e05159666b3bf8bc7a8a3a9eb1d518969eab529dd9b88c1ac684c25efa5c80")]
        [TestCase("f84f40058335880ca045a2036c39b7a7ae0113594ba92573c4f9762d874b918238e8aa0bf359abb57ba0bf7e331f7f7c1dd2e05159666b3bf8bc7a8a3a9eb1d518969eab529dd9b88c1ac684c25efa5c80")]
        [TestCase("f857408314095a88265e1ba765f911cca0154d890e264e75b2c5b1ef06bc81a71e0d615539ef6f77b13e37c3e09da68345a070cefc67ff52eb3e1ea9fc9e721d0458a952632927d2d7cb435b250c0c32e653c684e615830180")]
        [TestCase("f84f40058335880ca065c57060a8af10e007e1c45fb1f70cd4a9a61388da4d4c09ce29b69e2d5430d9a0bf7e331f7f7c1dd2e05159666b3bf8bc7a8a3a9eb1d518969eab529dd9b88c1ac684c25efa5c80")]
        [TestCase("f84f40058335880ca065c57060a8af10e007e1c45fb1f70cd4a9a61388da4d4c09ce29b69e2d5430d9a0bf7e331f7f7c1dd2e05159666b3bf8bc7a8a3a9eb1d518969eab529dd9b88c1ac684c25efa5c80")]
        [TestCase("f84f40058335880ca045a2036c39b7a7ae0113594ba92573c4f9762d874b918238e8aa0bf359abb57ba0bf7e331f7f7c1dd2e05159666b3bf8bc7a8a3a9eb1d518969eab529dd9b88c1ac684c25efa5c80")]
        [TestCase("f84f40058330fcc1a0b43dac825c7ef05ddf371e9e73bbad2aca1421752f411611fdb0de309d471a99a0bf7e331f7f7c1dd2e05159666b3bf8bc7a8a3a9eb1d518969eab529dd9b88c1ac684c25efa5c80")]
        [TestCase("f8524064863a0c5a2bccdaa0fb10500b905315227ab5309d7ecc4d3d89309628d8a36b91766a985ecb082ffaa0a62acdd14c72501b3bde21f1e0de7ab0830e1ecb24d5538d13096ad5f1c457d8c684116eb41380")]
        [TestCase("f84f40058335880ca045a2036c39b7a7ae0113594ba92573c4f9762d874b918238e8aa0bf359abb57ba0bf7e331f7f7c1dd2e05159666b3bf8bc7a8a3a9eb1d518969eab529dd9b88c1ac684c25efa5c80")]
        public void Can_deserialize_eth_64(string msgHex)
        {
            byte[] bytes = Bytes.FromHexString(msgHex);
            StatusMessageSerializer serializer = new();
            StatusMessage message = serializer.Deserialize(bytes);
            byte[] serialized = serializer.Serialize(message);
            serialized.Should().BeEquivalentTo(bytes);
            Assert.That(message.ProtocolVersion, Is.EqualTo(64), "ProtocolVersion");
        }

        [TestCase("f8524005830f0ea3 a01f895b10d62bf1b07c3aadb61d20d2568ba4617108e47435a0a911d1c5011614 a0bf7e331f7f7c1dd2e05159666b3bf8bc7a8a3a9eb1d518969eab529dd9b88c1ac9 84a3f5ab08 8317d433")]
        [TestCase("f8524005830f0ea3 a01f895b10d62bf1b07c3aadb61d20d2568ba4617108e47435a0a911d1c5011614 a0bf7e331f7f7c1dd2e05159666b3bf8bc7a8a3a9eb1d518969eab529dd9b88c1ac9 84a3f5ab08 8317d433")]
        [TestCase("f8524005830f0ea3 a01f895b10d62bf1b07c3aadb61d20d2568ba4617108e47435a0a911d1c5011614 a0bf7e331f7f7c1dd2e05159666b3bf8bc7a8a3a9eb1d518969eab529dd9b88c1ac9 84a3f5ab08 8317d433")]
        [TestCase("f8524005830f0ea3 a01f895b10d62bf1b07c3aadb61d20d2568ba4617108e47435a0a911d1c5011614 a0bf7e331f7f7c1dd2e05159666b3bf8bc7a8a3a9eb1d518969eab529dd9b88c1ac9 84a3f5ab08 8317d433")]
        [TestCase("f8524005830f0ea3 a01f895b10d62bf1b07c3aadb61d20d2568ba4617108e47435a0a911d1c5011614 a0bf7e331f7f7c1dd2e05159666b3bf8bc7a8a3a9eb1d518969eab529dd9b88c1ac9 84a3f5ab08 8317d433")]
        [TestCase("f8524005830f0ea3 a01f895b10d62bf1b07c3aadb61d20d2568ba4617108e47435a0a911d1c5011614 a0bf7e331f7f7c1dd2e05159666b3bf8bc7a8a3a9eb1d518969eab529dd9b88c1ac9 84a3f5ab08 8317d433")]

        [TestCase("f84f40058335880c a045a2036c39b7a7ae0113594ba92573c4f9762d874b918238e8aa0bf359abb57b a0bf7e331f7f7c1dd2e05159666b3bf8bc7a8a3a9eb1d518969eab529dd9b88c1ac6 84c25efa5c 80")]

        [TestCase("f8524005830f0ea3 a01f895b10d62bf1b07c3aadb61d20d2568ba4617108e47435a0a911d1c5011614 a0bf7e331f7f7c1dd2e05159666b3bf8bc7a8a3a9eb1d518969eab529dd9b88c1ac9 84a3f5ab08 8317d433")]
        [TestCase("f8524005830f0ea3 a01f895b10d62bf1b07c3aadb61d20d2568ba4617108e47435a0a911d1c5011614 a0bf7e331f7f7c1dd2e05159666b3bf8bc7a8a3a9eb1d518969eab529dd9b88c1ac9 84a3f5ab08 8317d433")]
        public void Can_deserialize_own_eth_64(string msgHex)
        {
            byte[] bytes = Bytes.FromHexString(msgHex.Replace(" ", string.Empty));
            StatusMessageSerializer serializer = new();
            StatusMessage message = serializer.Deserialize(bytes);
            byte[] serialized = serializer.Serialize(message);
            serialized.Should().BeEquivalentTo(bytes);
            Assert.That(message.ProtocolVersion, Is.EqualTo(64), "ProtocolVersion");
        }

        [Test]
        public void Can_deserialize_example_from_ethereumJ()
        {
            byte[] bytes = Bytes.FromHexString("f84927808425c60144a0832056d3c93ff2739ace7199952e5365aa29f18805be05634c4db125c5340216a0955f36d073ccb026b78ab3424c15cf966a7563aa270413859f78702b9e8e22cb");
            StatusMessageSerializer serializer = new();
            StatusMessage message = serializer.Deserialize(bytes);
            Assert.That(message.ProtocolVersion, Is.EqualTo(39), "ProtocolVersion");

            Assert.That((int)message.TotalDifficulty, Is.EqualTo(0x25c60144), "Difficulty");
            Assert.That(message.BestHash, Is.EqualTo(new Keccak("832056d3c93ff2739ace7199952e5365aa29f18805be05634c4db125c5340216")), "BestHash");
            Assert.That(message.GenesisHash, Is.EqualTo(new Keccak("0x955f36d073ccb026b78ab3424c15cf966a7563aa270413859f78702b9e8e22cb")), "GenesisHash");

            byte[] serialized = serializer.Serialize(message);
            Assert.That(serialized, Is.EqualTo(bytes), "serializing to same format");
        }

        [Test]
        public void To_string()
        {
            StatusMessage statusMessage = new();
            _ = statusMessage.ToString();
        }
    }
}
