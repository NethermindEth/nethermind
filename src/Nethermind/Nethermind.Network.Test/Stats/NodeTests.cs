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

using FluentAssertions;
using Nethermind.Core.Test.Builders;
using Nethermind.Stats.Model;
using NSubstitute;
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
            Node node = new Node(TestItem.PublicKeyA, "::ffff:73.224.122.50", 65535);
            Assert.AreEqual(65535, node.Port);
            Assert.AreEqual("73.224.122.50", node.Address.Address.MapToIPv4().ToString());
        }
        
        [Test]
        public void Not_equal_to_another_type()
        {
            Node node = new Node(TestItem.PublicKeyA, "::ffff:73.224.122.50", 65535);
            // ReSharper disable once SuspiciousTypeConversion.Global
            node.Equals(1).Should().BeFalse();
        }
        
        [TestCase("s")]
        [TestCase("c")]
        [TestCase("f")]
        [TestCase("zzz")]
        public void To_string_formats(string format)
        {
            Node node = new Node("127.0.0.1", 30303);
            node.ToString(format);
            _ = node.ToString();
            
            node = new Node("::ffff:127.0.0.1", 30303);
            node.ToString(format);
            _ = node.ToString();
        }
    }
}