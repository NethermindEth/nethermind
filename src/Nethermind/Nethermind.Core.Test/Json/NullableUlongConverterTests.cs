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
//  aulong? with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.IO;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    [TestFixture]
    public class NullableULongConverterTests : ConverterTestBase<ulong?>
    {
        [TestCase(NumberConversion.Hex)]
        [TestCase(NumberConversion.Decimal)]
        public void Test_roundtrip(NumberConversion numberConversion)
        {
            NullableULongConverter converter = new(numberConversion);
            TestConverter(int.MaxValue, (a, b) => a.Equals(b), converter);
            TestConverter(1L, (a, b) => a.Equals(b), converter);
            TestConverter(0L, (a, b) => a.Equals(b), converter);
        }

        [TestCase((NumberConversion)99)]
        public void Unknown_not_supported(NumberConversion notSupportedConversion)
        {
            NullableULongConverter converter = new(notSupportedConversion);
            Assert.Throws<NotSupportedException>(
                () => TestConverter(int.MaxValue, (a, b) => a.Equals(b), converter));
            Assert.Throws<NotSupportedException>(
                () => TestConverter(1L, (a, b) => a.Equals(b), converter));
        }

        [Test]
        public void Regression_0xa00000()
        {
            NullableULongConverter converter = new();
            JsonReader reader = new JsonTextReader(new StringReader("0xa00000"));
            reader.ReadAsString();
            ulong? result = converter.ReadJson(reader, typeof(ulong?), 0, false, JsonSerializer.CreateDefault());
            Assert.AreEqual(10485760, result);
        }

        [Test]
        public void Can_read_0x0()
        {
            NullableULongConverter converter = new();
            JsonReader reader = new JsonTextReader(new StringReader("0x0"));
            reader.ReadAsString();
            ulong? result = converter.ReadJson(reader, typeof(ulong?), 0L, false, JsonSerializer.CreateDefault());
            Assert.AreEqual(ulong.Parse("0"), result);
        }

        [Test]
        public void Can_read_0x000()
        {
            NullableULongConverter converter = new();
            JsonReader reader = new JsonTextReader(new StringReader("0x0000"));
            reader.ReadAsString();
            ulong? result = converter.ReadJson(reader, typeof(ulong?), 0L, false, JsonSerializer.CreateDefault());
            Assert.AreEqual(ulong.Parse("0"), result);
        }

        [Test]
        public void Can_read_0()
        {
            NullableULongConverter converter = new();
            JsonReader reader = new JsonTextReader(new StringReader("0"));
            reader.ReadAsString();
            ulong? result = converter.ReadJson(reader, typeof(ulong?), 0L, false, JsonSerializer.CreateDefault());
            Assert.AreEqual(ulong.Parse("0"), result);
        }

        [Test]
        public void Can_read_1()
        {
            NullableULongConverter converter = new();
            JsonReader reader = new JsonTextReader(new StringReader("1"));
            reader.ReadAsString();
            ulong? result = converter.ReadJson(reader, typeof(ulong?), 0L, false, JsonSerializer.CreateDefault());
            Assert.AreEqual(ulong.Parse("1"), result);
        }

        [Test]
        public void Can_read_null()
        {
            NullableULongConverter converter = new();
            JsonReader reader = new JsonTextReader(new StringReader("null"));
            reader.ReadAsString();
            ulong? result = converter.ReadJson(reader, typeof(ulong?), 0L, false, JsonSerializer.CreateDefault());
            Assert.AreEqual(null, result);
        }

        [Test]
        public void Throws_on_negative_numbers()
        {
            NullableULongConverter converter = new();
            JsonReader reader = new JsonTextReader(new StringReader("-1"));
            reader.ReadAsString();
            Assert.Throws<OverflowException>(
                () => converter.ReadJson(reader, typeof(ulong?), 0L, false, JsonSerializer.CreateDefault()));
        }
    }
}
