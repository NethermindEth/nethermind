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
using Nethermind.Serialization.Json;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    [TestFixture]
    public class LongConverterTests : ConverterTestBase<long>
    {
        [TestCase(NumberConversion.Hex)]
        [TestCase(NumberConversion.Decimal)]
        public void Test_roundtrip(NumberConversion numberConversion)
        {
            LongConverter converter = new LongConverter(numberConversion);
            TestConverter(int.MaxValue, (a, b) => a.Equals(b), converter);
            TestConverter(1L, (a, b) => a.Equals(b), converter);
            TestConverter(0L, (a, b) => a.Equals(b), converter);
        }
        
        [TestCase((NumberConversion)99)]
        public void Unknown_not_supported(NumberConversion notSupportedConversion)
        {
            LongConverter converter = new LongConverter(notSupportedConversion);
            Assert.Throws<NotSupportedException>(
                () => TestConverter(int.MaxValue, (a, b) => a.Equals(b), converter));
            Assert.Throws<NotSupportedException>(
                () => TestConverter(1L, (a, b) => a.Equals(b), converter));
        }

        [Test]
        public void Regression_0xa00000()
        {
            LongConverter converter = new LongConverter();
            JsonReader reader = new JsonTextReader(new StringReader("0xa00000"));
            reader.ReadAsString();
            long result = converter.ReadJson(reader, typeof(long), 0, false, JsonSerializer.CreateDefault());
            Assert.AreEqual(10485760, result);
        }
        
        [Test]
        public void Can_read_0x0()
        {
            LongConverter converter = new LongConverter();
            JsonReader reader = new JsonTextReader(new StringReader("0x0"));
            reader.ReadAsString();
            long result = converter.ReadJson(reader, typeof(long), 0L, false, JsonSerializer.CreateDefault());
            Assert.AreEqual(long.Parse("0"), result);
        }
        
        [Test]
        public void Can_read_0x000()
        {
            LongConverter converter = new LongConverter();
            JsonReader reader = new JsonTextReader(new StringReader("0x0000"));
            reader.ReadAsString();
            long result = converter.ReadJson(reader, typeof(long), 0L, false, JsonSerializer.CreateDefault());
            Assert.AreEqual(long.Parse("0"), result);
        }
        
        [Test]
        public void Can_read_0()
        {
            LongConverter converter = new LongConverter();
            JsonReader reader = new JsonTextReader(new StringReader("0"));
            reader.ReadAsString();
            long result = converter.ReadJson(reader, typeof(long), 0L, false, JsonSerializer.CreateDefault());
            Assert.AreEqual(long.Parse("0"), result);
        }
        
        [Test]
        public void Can_read_1()
        {
            LongConverter converter = new LongConverter();
            JsonReader reader = new JsonTextReader(new StringReader("1"));
            reader.ReadAsString();
            long result = converter.ReadJson(reader, typeof(long), 0L, false, JsonSerializer.CreateDefault());
            Assert.AreEqual(long.Parse("1"), result);
        }

        [Test]
        public void Throws_on_null()
        {
            LongConverter converter = new LongConverter();
            JsonReader reader = new JsonTextReader(new StringReader("null"));
            reader.ReadAsString();
            Assert.Throws<JsonException>(
                () => converter.ReadJson(reader, typeof(long), 0L, false, JsonSerializer.CreateDefault()));
        }
    }
}
