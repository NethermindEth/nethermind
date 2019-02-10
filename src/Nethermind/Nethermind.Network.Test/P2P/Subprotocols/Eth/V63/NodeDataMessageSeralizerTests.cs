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

using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V63
{
    [TestFixture]
    public class NodeDataMessageSerializerTests
    {
        private static void Test(byte[][] data)
        {
            NodeDataMessage message = new NodeDataMessage(data);
            NodeDataMessageSerializer serializer = new NodeDataMessageSerializer();
            var serialized = serializer.Serialize(message);
            NodeDataMessage deserialized = serializer.Deserialize(serialized);

            if (data == null)
            {
                Assert.AreEqual(0, deserialized.Data.Length);
            }
            else
            {
                Assert.AreEqual(data.Length, deserialized.Data.Length, "length");
                for (int i = 0; i < data.Length; i++) Assert.AreEqual(data[i] ?? new byte[0], deserialized.Data[i], $"data[{i}]");
            }
        }

        [Test]
        public void Roundtrip()
        {
            byte[][] data = {TestItem.KeccakA.Bytes, TestItem.KeccakB.Bytes, TestItem.KeccakC.Bytes};
            Test(data);
        }

        [Test]
        public void Roundtrip_with_null_top_level()
        {
            Test(null);
        }

        [Test]
        public void Roundtrip_with_nulls()
        {
            byte[][] data = {TestItem.KeccakA.Bytes, null, TestItem.KeccakC.Bytes};
            Test(data);
        }
    }
}