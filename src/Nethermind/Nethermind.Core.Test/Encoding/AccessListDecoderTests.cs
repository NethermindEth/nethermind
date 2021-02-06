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

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip2930;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding
{
    [TestFixture]
    public class AccessListDecoderTests
    {
        private readonly AccessListDecoder _decoder = new AccessListDecoder();

        public static IEnumerable<(string, AccessList)> TestCaseSource()
        {
            yield return ("null", null);

            HashSet<UInt256> indexes = new HashSet<UInt256>();
            Dictionary<Address, IReadOnlySet<UInt256>> data = new Dictionary<Address, IReadOnlySet<UInt256>>();
            // yield return ("empty", new AccessList(data)); <-- null and empty are equivalent here
            //
            indexes = new HashSet<UInt256>();
            data = new Dictionary<Address, IReadOnlySet<UInt256>>();
            data.Add(TestItem.AddressA, indexes);
            yield return ("no storage", new AccessList(data));

            indexes = new HashSet<UInt256>();
            data = new Dictionary<Address, IReadOnlySet<UInt256>>();
            data.Add(TestItem.AddressA, indexes);
            data.Add(TestItem.AddressB, indexes);
            yield return ("no storage 2", new AccessList(data));

            indexes = new HashSet<UInt256>();
            data = new Dictionary<Address, IReadOnlySet<UInt256>>();
            data.Add(TestItem.AddressA, indexes);
            indexes.Add(1);
            yield return ("1-1", new AccessList(data));

            indexes = new HashSet<UInt256>();
            data = new Dictionary<Address, IReadOnlySet<UInt256>>();
            data.Add(TestItem.AddressA, indexes);
            indexes.Add(1);
            indexes.Add(2);
            yield return ("1-2", new AccessList(data));

            indexes = new HashSet<UInt256>();
            data = new Dictionary<Address, IReadOnlySet<UInt256>>();
            indexes.Add(1);
            indexes.Add(2);
            data.Add(TestItem.AddressA, indexes);
            data.Add(TestItem.AddressB, indexes);
            yield return ("2-2", new AccessList(data));

            indexes = new HashSet<UInt256>();
            var indexes2 = new HashSet<UInt256>();
            data = new Dictionary<Address, IReadOnlySet<UInt256>>();
            indexes.Add(1);
            indexes2.Add(2);
            data.Add(TestItem.AddressA, indexes);
            data.Add(TestItem.AddressB, indexes2);
            AccessList accessList = new AccessList(data,
                new Queue<object>(new List<object> {TestItem.AddressA, (UInt256)1, TestItem.AddressB, (UInt256)2}));
            yield return ("with order queue", accessList);
            
            indexes = new HashSet<UInt256>();
            indexes2 = new HashSet<UInt256>();
            data = new Dictionary<Address, IReadOnlySet<UInt256>>();
            indexes.Add(1);
            indexes2.Add(2);
            data.Add(TestItem.AddressA, indexes);
            data.Add(TestItem.AddressB, indexes2);
            accessList = new AccessList(data,
                new Queue<object>(new List<object> {TestItem.AddressA, (UInt256)1, (UInt256)1, TestItem.AddressB, (UInt256)2, TestItem.AddressB, (UInt256)2}));
            yield return ("with order queue and duplicates", accessList);
        }

        [TestCaseSource(nameof(TestCaseSource))]
        public void Roundtrip((string TestName, AccessList AccessList) testCase)
        {
            RlpStream rlpStream = new RlpStream(10000);
            _decoder.Encode(rlpStream, testCase.AccessList);
            rlpStream.Position = 0;
            AccessList decoded = _decoder.Decode(rlpStream);
            if (testCase.AccessList is null)
            {
                decoded.Should().BeNull();
            }
            else
            {
                decoded!.Data.Should().BeEquivalentTo(testCase.AccessList.Data, testCase.TestName);
            }
        }

        [TestCaseSource(nameof(TestCaseSource))]
        public void Roundtrip_value((string TestName, AccessList AccessList) testCase)
        {
            RlpStream rlpStream = new RlpStream(10000);
            _decoder.Encode(rlpStream, testCase.AccessList);
            rlpStream.Position = 0;
            Rlp.ValueDecoderContext ctx = rlpStream.Data.AsRlpValueContext();
            AccessList decoded = _decoder.Decode(ref ctx);
            if (testCase.AccessList is null)
            {
                decoded.Should().BeNull();
            }
            else
            {
                decoded!.Data.Should().BeEquivalentTo(testCase.AccessList.Data, testCase.TestName);
            }
        }
        
        [Test]
        public void Get_length_returns_1_for_null()
        {
            _decoder.GetLength(null, RlpBehaviors.None).Should().Be(1);
        }
    }
}
