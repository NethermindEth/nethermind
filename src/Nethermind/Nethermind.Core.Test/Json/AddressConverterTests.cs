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

using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    [TestFixture]
    public class AddressConverterTests : ConverterTestBase<Address>
    {
        [Test]
        public void Null_value()
        {
            TestConverter(null, (address, address1) => address == address1, new AddressConverter());
        }
        
        [Test]
        public void Zero_value()
        {
            TestConverter(Address.Zero, (address, address1) => address == address1, new AddressConverter());
        }
        
        [Test]
        public void Some_value()
        {
            TestConverter(TestItem.AddressA, (address, address1) => address == address1, new AddressConverter());
        }
    }
}
