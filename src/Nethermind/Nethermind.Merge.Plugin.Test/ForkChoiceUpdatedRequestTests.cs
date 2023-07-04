// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Test.Builders;
using Nethermind.Merge.Plugin.Data;
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
