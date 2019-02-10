/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V63
{
    [TestFixture]
    public class GetNodeDataMessageSerializerTests
    {
        private static void Test(Keccak[] keys)
        {
            GetNodeDataMessage message = new GetNodeDataMessage(keys);
            GetNodeDataMessageSerializer serializer = new GetNodeDataMessageSerializer();
            var serialized = serializer.Serialize(message);
            GetNodeDataMessage deserialized = serializer.Deserialize(serialized);

            Assert.AreEqual(keys.Length, deserialized.Keys.Length, "length");
            for (int i = 0; i < keys.Length; i++) Assert.AreEqual(keys[i], deserialized.Keys[i], $"keys[{i}]");
        }

        [Test]
        public void Roundtrip()
        {
            Keccak[] keys = {TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC};
            Test(keys);
        }

        [Test]
        public void Roundtrip_with_nulls()
        {
            Keccak[] keys = {null, TestItem.KeccakA, null, TestItem.KeccakB, null, null};
            Test(keys);
        }
    }
}