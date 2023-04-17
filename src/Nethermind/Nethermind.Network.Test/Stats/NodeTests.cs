// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Test.Builders;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Test.Stats
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class NodeTests
    {
        [Test]
        public void Can_parse_ipv6_prefixed_ip()
        {
            Node node = new(TestItem.PublicKeyA, "::ffff:73.224.122.50", 65535);
            Assert.AreEqual(65535, node.Port);
            Assert.AreEqual("73.224.122.50", node.Address.Address.MapToIPv4().ToString());
        }

        [Test]
        public void Not_equal_to_another_type()
        {
            Node node = new(TestItem.PublicKeyA, "::ffff:73.224.122.50", 65535);
            // ReSharper disable once SuspiciousTypeConversion.Global
            node.Equals(1).Should().BeFalse();
        }

        [TestCase("s", "      127.0.0.1:30303")]
        [TestCase("c", "[Node|      127.0.0.1:30303|Details|ClientId]")]
        [TestCase("f", "enode://a49ac7010c2e0a444dfeeabadbafa4856ba4a2d732acb86d20c577b3b365fdaeb0a70ce47f890cf2f9fca562a7ed784f76eb870a2c75c0f2ab476a70ccb67e92@      127.0.0.1:30303|ClientId")]
        [TestCase("e", "enode://a49ac7010c2e0a444dfeeabadbafa4856ba4a2d732acb86d20c577b3b365fdaeb0a70ce47f890cf2f9fca562a7ed784f76eb870a2c75c0f2ab476a70ccb67e92@      127.0.0.1:30303")]
        [TestCase("p", "enode://a49ac7010c2e0a444dfeeabadbafa4856ba4a2d732acb86d20c577b3b365fdaeb0a70ce47f890cf2f9fca562a7ed784f76eb870a2c75c0f2ab476a70ccb67e92@      127.0.0.1:30303|0xb7705ae4c6f81b66cdb323c65f4e8133690fc099")]
        [TestCase("zzz", "enode://a49ac7010c2e0a444dfeeabadbafa4856ba4a2d732acb86d20c577b3b365fdaeb0a70ce47f890cf2f9fca562a7ed784f76eb870a2c75c0f2ab476a70ccb67e92@      127.0.0.1:30303")]
        public void To_string_formats(string format, string expectedFormat)
        {
            Node GetNode(string host) =>
                new(TestItem.PublicKeyA, host, 30303) { ClientId = "ClientId", EthDetails = "Details" };

            Node node = GetNode("127.0.0.1");
            node.ToString(format).Should().Be(expectedFormat);

            node = GetNode("::ffff:127.0.0.1");
            node.ToString(format).Should().Be(expectedFormat);
        }


    }
}
