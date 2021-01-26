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

using Nethermind.Network.P2P;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class PingMessageSerializerTests
    {
        [Test]
        public void Can_do_roundtrip()
        {
            PingMessage msg = PingMessage.Instance;
            PingMessageSerializer serializer = new PingMessageSerializer();
            byte[] serialized = serializer.Serialize(msg);
            Assert.AreEqual(0xc0, serialized[0]);
            PingMessage deserialized = serializer.Deserialize(serialized);
            Assert.NotNull(deserialized);
        }
    }
}
