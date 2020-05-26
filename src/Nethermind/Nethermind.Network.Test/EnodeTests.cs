//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Linq;
using System.Net;
using DotNetty.Common.Utilities;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Network.Test
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
    }
}