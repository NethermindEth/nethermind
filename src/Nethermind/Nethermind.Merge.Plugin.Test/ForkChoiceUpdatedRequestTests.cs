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
// 

using FluentAssertions;
using Nethermind.Core.Test.Builders;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Data.V1;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test
{
    public class ForkChoiceUpdatedRequestTests
    {
        private readonly IJsonSerializer _serializer = new EthereumJsonSerializer();

        [Test]
        public void serialization_and_deserialization_roundtrip()
        {
            ForkchoiceStateV1 initial = new(TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC);
            string? serialized = _serializer.Serialize(initial);
            ForkchoiceStateV1 deserialized = _serializer.Deserialize<ForkchoiceStateV1>(serialized);
            deserialized.Should().BeEquivalentTo(initial);
        }
    }
}
