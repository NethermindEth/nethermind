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
using Nethermind.Network.P2P.Subprotocols.Tru;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Tru
{
    [TestFixture]
    public class GetReceiptsMessageSerializerTests
    {
        private static void Test(Keccak[] keys)
        {
            GetReceiptsMessage message = new GetReceiptsMessage(keys);
            GetReceiptsMessageSerializer serializer = new GetReceiptsMessageSerializer();
            var serialized = serializer.Serialize(message);
            GetReceiptsMessage deserialized = serializer.Deserialize(serialized);

            Assert.AreEqual(keys.Length, deserialized.BlockHashes.Length, "length");
            for (int i = 0; i < keys.Length; i++) Assert.AreEqual(keys[i], deserialized.BlockHashes[i], $"blockHashes[{i}]");
        }

        [Test]
        public void Roundtrip()
        {
            Keccak[] hashes = {TestObject.KeccakA, TestObject.KeccakB, TestObject.KeccakC};
            Test(hashes);
        }

        [Test]
        public void Roundtrip_with_nulls()
        {
            Keccak[] hashes = {null, TestObject.KeccakA, null, TestObject.KeccakB, null, null};
            Test(hashes);
        }
    }
}