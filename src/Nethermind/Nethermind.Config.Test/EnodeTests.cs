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

using System;
using System.Collections;
using System.Net;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Network;
using NUnit.Framework;

namespace Nethermind.Config.Test
{
    public class EnodeTests
    {
        [Test]
        public void ip_test()
        {
            var publicKey = new PublicKey("0x000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f");
            Enode enode = new Enode($"enode://{publicKey.ToString(false)}@{IPAddress.Loopback}:{1234}");
            enode.HostIp.Should().BeEquivalentTo(IPAddress.Loopback);
            enode.Port.Should().Be(1234);
            enode.PublicKey.Should().BeEquivalentTo(publicKey);
        }
        
        [Test]
        public void dns_test()
        {
            var publicKey = new PublicKey("0x000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f");
            string domain = "nethermind.io";
            Enode enode = new Enode($"enode://{publicKey.ToString(false)}@{domain}:{1234}");
            Dns.GetHostAddresses(domain).Should().NotBeEmpty();
            enode.Port.Should().Be(1234);
            enode.PublicKey.Should().BeEquivalentTo(publicKey);
        }
        
        [Test]
        public void dns_test_wrong_domain()
        {
            var publicKey = new PublicKey("0x000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f");
            string domain = "i_do_not_exist";
            Action action = () => new Enode($"enode://{publicKey.ToString(false)}@{domain}:{1234}");
            action.Should().Throw<ArgumentException>();
        }

        public static IEnumerable Ipv4vs6TestCases
        {
            get
            {
                var ipv6_1 = IPAddress.Parse("2607:f8b0:4002:c02::6a");
                var ipv6_2 = IPAddress.Parse("2607:f8b0:4002:c02::67");
                var ipv4 = IPAddress.Parse("172.217.12.36");
                yield return new TestCaseData(new object[] {new[] {ipv4}}).Returns(ipv4);
                yield return new TestCaseData(new object[] {new[] {ipv6_1, ipv6_2, ipv4}}).Returns(ipv4);
                yield return new TestCaseData(new object[] {new[] {ipv4, ipv6_1, ipv6_2}}).Returns(ipv4);
                yield return new TestCaseData(new object[] {new[] {ipv6_1, ipv6_2}}).Returns(ipv6_1.MapToIPv4());
            }
        }

        [TestCaseSource(nameof(Ipv4vs6TestCases))]
        public IPAddress can_find_ipv4_host(IPAddress[] ips) => Enode.GetHostIpFromDnsAddresses(ips);
    }
}
