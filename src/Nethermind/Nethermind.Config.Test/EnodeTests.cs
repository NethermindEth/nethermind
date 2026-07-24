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

        public static IEnumerable Ipv4vs6TestCases
        {
            get
            {
                IPAddress ipv6_1 = IPAddress.Parse("2607:f8b0:4002:c02::6a");
                IPAddress ipv6_2 = IPAddress.Parse("2607:f8b0:4002:c02::67");
                IPAddress ipv4 = IPAddress.Parse("172.217.12.36");
                yield return new TestCaseData(new object[] { new[] { ipv4 } }).Returns(ipv4);
                yield return new TestCaseData(new object[] { new[] { ipv6_1, ipv6_2, ipv4 } }).Returns(ipv4);
                yield return new TestCaseData(new object[] { new[] { ipv4, ipv6_1, ipv6_2 } }).Returns(ipv4);
                yield return new TestCaseData(new object[] { new[] { ipv6_1, ipv6_2 } }).Returns(ipv6_1.MapToIPv4());
            }
        }

        [TestCaseSource(nameof(Ipv4vs6TestCases))]
        public IPAddress? can_find_ipv4_host(IPAddress[] ips) => Enode.GetHostIpFromDnsAddresses(ips);
    }
}
