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
using System.Text;
using FluentAssertions;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Data
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class IdConverterTests : SerializationTestBase
    {
        [Test]
        public void Can_do_roundtrip_big()
        {
            TestRoundtrip<SomethingWithId>("{\"id\":123498132871289317239813219}");
        }
        
        [Test]
        public void Can_handle_int()
        {
            IdConverter converter = new IdConverter();
            converter.WriteJson(new JsonTextWriter(new StringWriter()), 1, JsonSerializer.CreateDefault());
        }
        
        [Test]
        public void Throws_on_writing_decimal()
        {
            IdConverter converter = new IdConverter();
            Assert.Throws<NotSupportedException>(
                () => converter.WriteJson(new JsonTextWriter(new StringWriter()), 1.1, JsonSerializer.CreateDefault()));
        }
        
        [TestCase(typeof(int))]
        [TestCase(typeof(string))]
        [TestCase(typeof(long))]
        [TestCase(typeof(BigInteger))]
        [TestCase(typeof(BigInteger?))]
        [TestCase(typeof(UInt256?))]
        [TestCase(typeof(UInt256))]
        public void It_supports_the_types_that_it_needs_to_support(Type type)
        {
            IdConverter converter = new IdConverter();
            converter.CanConvert(type).Should().Be(true);
        }
        
        [TestCase(typeof(object))]
        [TestCase(typeof(IdConverterTests))]
        public void It_supports_all_silly_types_and_we_can_live_with_it(Type type)
        {
            IdConverter converter = new IdConverter();
            converter.CanConvert(type).Should().Be(true);
        }

        [Test]
        public void Can_do_roundtrip_long()
        {
            TestRoundtrip<SomethingWithId>("{\"id\":1234}");
        }

        [Test]
        public void Can_do_roundtrip_string()
        {
            TestRoundtrip<SomethingWithId>("{\"id\":\"asdasdasd\"}");
        }

        [Test]
        public void Can_do_roundtrip_null()
        {
            TestRoundtrip<SomethingWithId>("{\"id\":null}");
        }
        
        [Test]
        public void Decimal_not_supported()
        {
            Assert.Throws<NotSupportedException>(() =>
                TestRoundtrip<SomethingWithId>("{\"id\":2.1}"));
            
            Assert.Throws<NotSupportedException>(() =>
                TestRoundtrip<SomethingWithDecimalId>("{\"id\":2.1}"));
        }

        // ReSharper disable once ClassNeverInstantiated.Global
        // ReSharper disable once MemberCanBePrivate.Global
        public class SomethingWithId
        {
            [JsonConverter(typeof(IdConverter))]
            [JsonProperty(NullValueHandling = NullValueHandling.Include)]
            public object Id { get; set; }

            public string Something { get; set; }
        }
        
        public class SomethingWithDecimalId
        {
            [JsonConverter(typeof(IdConverter))]
            [JsonProperty(NullValueHandling = NullValueHandling.Include)]
            public decimal Id { get; set; }

            public string Something { get; set; }
        }
    }
}
