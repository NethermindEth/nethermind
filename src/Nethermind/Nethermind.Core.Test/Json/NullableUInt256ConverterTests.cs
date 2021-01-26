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

using System.IO;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    [TestFixture]
    public class NullableUInt256ConverterTests : ConverterTestBase<UInt256?>
    {
        [TestCase(NumberConversion.Hex)]
        [TestCase(NumberConversion.Decimal)]
        public void Test_roundtrip(NumberConversion numberConversion)
        {
            NullableUInt256Converter converter = new NullableUInt256Converter(numberConversion);
            TestConverter(null, (integer, bigInteger) => integer.Equals(bigInteger), converter);
            TestConverter(int.MaxValue, (integer, bigInteger) => integer.Equals(bigInteger), converter);
            TestConverter(UInt256.One, (integer, bigInteger) => integer.Equals(bigInteger), converter);
            TestConverter(UInt256.Zero, (integer, bigInteger) => integer.Equals(bigInteger), converter);
        }
        
        [Test]
        public void Regression_0xa00000()
        {
            NullableUInt256Converter converter = new NullableUInt256Converter();
            JsonReader reader = new JsonTextReader(new StringReader("0xa00000"));
            reader.ReadAsString();
            UInt256? result = converter.ReadJson(reader, typeof(UInt256?), UInt256.Zero, false, JsonSerializer.CreateDefault());
            Assert.AreEqual(UInt256.Parse("10485760"), result);
        }
        
        [Test]
        public void Can_read_0x0()
        {
            NullableUInt256Converter converter = new NullableUInt256Converter();
            JsonReader reader = new JsonTextReader(new StringReader("0x0"));
            reader.ReadAsString();
            UInt256? result = converter.ReadJson(reader, typeof(UInt256?), UInt256.Zero, false, JsonSerializer.CreateDefault());
            Assert.AreEqual(UInt256.Parse("0"), result);
        }
        
        [Test]
        public void Can_read_0()
        {
            NullableUInt256Converter converter = new NullableUInt256Converter();
            JsonReader reader = new JsonTextReader(new StringReader("0"));
            reader.ReadAsString();
            UInt256? result = converter.ReadJson(reader, typeof(UInt256?), UInt256.Zero, false, JsonSerializer.CreateDefault());
            Assert.AreEqual(UInt256.Parse("0"), result);
        }
        
        [Test]
        public void Can_read_1()
        {
            NullableUInt256Converter converter = new NullableUInt256Converter();
            JsonReader reader = new JsonTextReader(new StringReader("1"));
            reader.ReadAsString();
            UInt256? result = converter.ReadJson(reader, typeof(UInt256?), UInt256.Zero, false, JsonSerializer.CreateDefault());
            Assert.AreEqual(UInt256.Parse("1"), result);
        }
    }
}
