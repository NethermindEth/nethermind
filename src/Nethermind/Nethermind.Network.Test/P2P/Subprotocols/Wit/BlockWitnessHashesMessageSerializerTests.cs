// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Wit.Messages;
using Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Wit
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class BlockWitnessHashesMessageSerializerTests
    {
        [Test]
        public void Can_handle_zero()
        {
            BlockWitnessHashesMessageSerializer serializer = new();
            BlockWitnessHashesMessage message = new(1, new Keccak[0]);
            SerializerTester.TestZero(serializer, message);
        }

        [Test]
        public void Can_handle_one()
        {
            BlockWitnessHashesMessageSerializer serializer = new();
            BlockWitnessHashesMessage message = new(1, new[] { Keccak.Zero });
            SerializerTester.TestZero(serializer, message);
        }

        [Test]
        public void Can_handle_many()
        {
            BlockWitnessHashesMessageSerializer serializer = new();
            BlockWitnessHashesMessage message = new(1, TestItem.Keccaks);
            SerializerTester.TestZero(serializer, message);
        }

        [Test]
        public void Can_handle_null()
        {
            BlockWitnessHashesMessageSerializer serializer = new();
            BlockWitnessHashesMessage message = new(1, null);
            byte[] serialized = serializer.Serialize(message);
            serialized[0].Should().Be(194);
            serializer.Deserialize(serialized);
        }
    }
}
