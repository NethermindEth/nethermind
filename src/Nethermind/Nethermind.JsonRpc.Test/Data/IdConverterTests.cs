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

        // ReSharper disable once ClassNeverInstantiated.Global
        // ReSharper disable once MemberCanBePrivate.Global
        public class SomethingWithId
        {
            [JsonConverter(typeof(IdConverter))]
            [JsonProperty(NullValueHandling = NullValueHandling.Include)]
            public object Id { get; set; }

            public string Something { get; set; }
        }
    }
}