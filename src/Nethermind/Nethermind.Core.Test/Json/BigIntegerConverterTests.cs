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

using System;
using System.IO;
using System.Numerics;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    [TestFixture]
    public class BigIntegerConverterTests : ConverterTestBase<BigInteger>
    {
        [TestCase(NumberConversion.Hex)]
        [TestCase(NumberConversion.Raw)]
        [TestCase(NumberConversion.Decimal)]
        public void Test_roundtrip(NumberConversion numberConversion)
        {
            BigIntegerConverter converter = new BigIntegerConverter(numberConversion);
            TestConverter(int.MaxValue, (integer, bigInteger) => integer.Equals(bigInteger), converter);
            TestConverter(BigInteger.One, (integer, bigInteger) => integer.Equals(bigInteger), converter);
            TestConverter(BigInteger.Zero, (integer, bigInteger) => integer.Equals(bigInteger), converter);
        }
        
        [Test]
        public void Regression_0xa00000()
        {
            BigIntegerConverter converter = new BigIntegerConverter();
            JsonReader reader = new JsonTextReader(new StringReader("0xa00000"));
            reader.ReadAsString();
            BigInteger result = converter.ReadJson(reader, typeof(BigInteger), BigInteger.Zero, false, JsonSerializer.CreateDefault());
            Assert.AreEqual(BigInteger.Parse("10485760"), result);
        }
        
        [TestCase((NumberConversion)99)]
        public void Unknown_not_supported(NumberConversion notSupportedConversion)
        {
            BigIntegerConverter converter = new BigIntegerConverter(notSupportedConversion);
            Assert.Throws<NotSupportedException>(
                () => TestConverter(int.MaxValue, (a, b) => a.Equals(b), converter));
            Assert.Throws<NotSupportedException>(
                () => TestConverter(1L, (a, b) => a.Equals(b), converter));
        }
        
        [Test]
        public void Can_read_0x0()
        {
            BigIntegerConverter converter = new BigIntegerConverter();
            JsonReader reader = new JsonTextReader(new StringReader("0x0"));
            reader.ReadAsString();
            BigInteger result = converter.ReadJson(reader, typeof(BigInteger), BigInteger.Zero, false, JsonSerializer.CreateDefault());
            Assert.AreEqual(BigInteger.Parse("0"), result);
        }
        
        [Test]
        public void Can_read_0()
        {
            BigIntegerConverter converter = new BigIntegerConverter();
            JsonReader reader = new JsonTextReader(new StringReader("0"));
            reader.ReadAsString();
            BigInteger result = converter.ReadJson(reader, typeof(BigInteger), BigInteger.Zero, false, JsonSerializer.CreateDefault());
            Assert.AreEqual(BigInteger.Parse("0"), result);
        }
        
        [Test]
        public void Can_read_1()
        {
            BigIntegerConverter converter = new BigIntegerConverter();
            JsonReader reader = new JsonTextReader(new StringReader("1"));
            reader.ReadAsString();
            BigInteger result = converter.ReadJson(reader, typeof(BigInteger), BigInteger.Zero, false, JsonSerializer.CreateDefault());
            Assert.AreEqual(BigInteger.Parse("1"), result);
        }
    }
}
