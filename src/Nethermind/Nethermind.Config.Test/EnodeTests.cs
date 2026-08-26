// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Net;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Config.Test
{
    public class EnodeTests
    {
        [Test]
        public void ip_test()
        {
            PublicKey publicKey = new("0x000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f");
            Enode enode = new($"enode://{publicKey.ToString(false)}@{IPAddress.Loopback}:{1234}");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(enode.HostIp, Is.EqualTo(IPAddress.Loopback));
                Assert.That(enode.Port, Is.EqualTo(1234));
                Assert.That(enode.PublicKey, Is.EqualTo(publicKey));
            }
        }

        [Test]
        public void dns_test()
        {
            PublicKey publicKey = new("0x000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f");
            string domain = "nethermind.io";
            Enode enode = new($"enode://{publicKey.ToString(false)}@{domain}:{1234}");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(Dns.GetHostAddresses(domain), Is.Not.Empty);
                Assert.That(enode.Port, Is.EqualTo(1234));
                Assert.That(enode.PublicKey, Is.EqualTo(publicKey));
            }
        }

        [Test]
        public void dns_test_wrong_domain()
        {
            PublicKey publicKey = new("0x000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f");
            string domain = "i_do_not_exist";
            Action action = () => _ = new Enode($"enode://{publicKey.ToString(false)}@{domain}:{1234}");
            Assert.That(action, Throws.TypeOf<ArgumentException>());
        }

        [Test]
        public void info_normalizes_ipv4_mapped_ipv6_host()
        {
            PublicKey publicKey = new("0x000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f");
            IPAddress mapped = IPAddress.Parse("::ffff:192.168.2.54");
            Enode enode = new(publicKey, mapped, 0, 40306);

            // HostIp is preserved verbatim (matches legacy persisted-node decoding behavior)...
            Assert.That(enode.HostIp, Is.EqualTo(mapped));

            // ...but Info must bracket-free normalize it to plain IPv4, otherwise it produces an
            // invalid URI that fails to reload as a persisted node on the next startup.
            Assert.That(Enode.IsEnode(enode.Info, out _), Is.True);
            Enode reparsed = new(enode.Info);
            Assert.That(reparsed.HostIp, Is.EqualTo(IPAddress.Parse("192.168.2.54")));
        }

        [TestCase("fd00:beef:cafe::11", 30304, "@[fd00:beef:cafe::11]:30303?discport=30304")]
        [TestCase("fd00:beef:cafe::11", 30303, "@[fd00:beef:cafe::11]:30303")]
        [TestCase("::ffff:172.217.12.36", 30303, "@172.217.12.36:30303")]
        public void info_formats_host_for_reparsing(string host, int discoveryPort, string expectedTail)
        {
            PublicKey publicKey = new("0x000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f");
            IPAddress hostIp = IPAddress.Parse(host);
            Enode enode = new(publicKey, hostIp, 30303, discoveryPort);

            Assert.That(enode.Info, Does.EndWith(expectedTail));
            Assert.That(Enode.IsEnode(enode.Info, out _), Is.True);
            Enode reparsed = new(enode.Info);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(reparsed.HostIp, Is.EqualTo(hostIp.IsIPv4MappedToIPv6 ? hostIp.MapToIPv4() : hostIp));
                Assert.That(reparsed.Port, Is.EqualTo(30303));
                Assert.That(reparsed.DiscoveryPort, Is.EqualTo(discoveryPort));
            }
        }

        [Test]
        public void can_parse_bracketed_native_ipv6_host()
        {
            PublicKey publicKey = new("0x000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f");
            IPAddress ipv6 = IPAddress.Parse("fd00:beef:cafe::11");

            Enode enode = new($"enode://{publicKey.ToString(false)}@[fd00:beef:cafe::11]:30303?discport=30304");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(enode.HostIp, Is.EqualTo(ipv6));
                Assert.That(enode.Port, Is.EqualTo(30303));
                Assert.That(enode.DiscoveryPort, Is.EqualTo(30304));
                Assert.That(enode.PublicKey, Is.EqualTo(publicKey));
            }
        }

        [TestCase("/junk")]
        [TestCase("#?discport=30304")]
        [TestCase("?discport=-1")]
        [TestCase("?discport=65536")]
        [TestCase("?discport=+30304")]
        public void rejects_malformed_enode_suffix(string suffix)
        {
            PublicKey publicKey = new("0x000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f");

            Assert.That(
                () => new Enode($"enode://{publicKey.ToString(false)}@127.0.0.1:30303{suffix}"),
                Throws.ArgumentException);
        }

        public static IEnumerable Ipv4vs6TestCases
        {
            get
            {
                IPAddress ipv6_1 = IPAddress.Parse("2607:f8b0:4002:c02::6a");
                IPAddress ipv6_2 = IPAddress.Parse("2607:f8b0:4002:c02::67");
                IPAddress ipv4 = IPAddress.Parse("172.217.12.36");
                IPAddress ipv4Mapped = IPAddress.Parse("::ffff:172.217.12.36");
                yield return new TestCaseData(new object[] { new[] { ipv4 } }).Returns(ipv4);
                yield return new TestCaseData(new object[] { new[] { ipv6_1, ipv6_2, ipv4 } }).Returns(ipv4);
                yield return new TestCaseData(new object[] { new[] { ipv4, ipv6_1, ipv6_2 } }).Returns(ipv4);
                yield return new TestCaseData(new object[] { new[] { ipv6_1, ipv6_2 } }).Returns(ipv6_1);
                yield return new TestCaseData(new object[] { new[] { ipv4Mapped } }).Returns(ipv4);
                yield return new TestCaseData(new object[] { new[] { ipv6_1, ipv4Mapped } }).Returns(ipv4);
            }
        }

        [TestCaseSource(nameof(Ipv4vs6TestCases))]
        public IPAddress? can_find_ipv4_host(IPAddress[] ips) => Enode.GetHostIpFromDnsAddresses(ips);
    }
}
