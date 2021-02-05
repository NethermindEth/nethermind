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
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding
{
    [TestFixture]
    public class AccessListDecoderTests
    {
        private readonly AccessListDecoder _decoder = new AccessListDecoder();
        
        public static IEnumerable<AccessList> TestCaseSource()
        {
            yield return null;

            HashSet<UInt256> indexes = new HashSet<UInt256>();
            Dictionary<Address, IReadOnlySet<UInt256>> data = new Dictionary<Address, IReadOnlySet<UInt256>>();
            // yield return new AccessList(data);
            
            data.Add(Address.Zero, indexes);
            yield return new AccessList(data);
            
            indexes.Add(1);
            data.Add(TestItem.AddressA, indexes);
            yield return new AccessList(data);
            
            indexes.Add(2);
            yield return new AccessList(data);
            
            data.Add(TestItem.AddressB, indexes);
            yield return new AccessList(data);
        }
        
        [TestCaseSource(nameof(TestCaseSource))]
        public void Roundtrip(AccessList accessList)
        {
            RlpStream rlpStream = new RlpStream(10000);
            _decoder.Encode(rlpStream, accessList, RlpBehaviors.UseTransactionTypes);
            rlpStream.Position = 0;
            AccessList decoded = _decoder.Decode(rlpStream, RlpBehaviors.UseTransactionTypes);
            decoded.Should().BeEquivalentTo(accessList);
        }
    }
}
