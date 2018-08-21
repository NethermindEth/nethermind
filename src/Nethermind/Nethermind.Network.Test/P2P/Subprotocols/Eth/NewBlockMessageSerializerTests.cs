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

using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth
{
    [TestFixture]
    public class NewBlockMessageSerializerTests
    {
        [Test]
        public void Roundtrip()
        {
            NewBlockMessage message = new NewBlockMessage();
            message.TotalDifficulty = 131200;
            message.Block = Build.A.Block.Genesis.TestObject;
            NewBlockMessageSerializer serializer = new NewBlockMessageSerializer();
            byte[] bytes = serializer.Serialize(message);
            byte[] expectedBytes = Bytes.FromHexString("f90205f901fef901f9a00000000000000000000000000000000000000000000000000000000000000000a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000830f424080833d090080830f424083010203a000000000000000000000000000000000000000000000000000000000000000008800000000000003e8c0c083020080");

            Assert.True(Bytes.AreEqual(bytes, expectedBytes), "bytes");
            
            NewBlockMessage deserialized = serializer.Deserialize(bytes);
            Assert.AreEqual(message.TotalDifficulty, deserialized.TotalDifficulty, "total difficulty");
            Assert.AreEqual(message.Block.Hash, deserialized.Block.Hash, "hash");
        }
    }
}