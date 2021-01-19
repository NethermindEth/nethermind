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

using Nethermind.Core.Extensions;
using Nethermind.Network.P2P;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class DisconnectMessageSerializerTests
    {
        [Test]
        public void Can_do_roundtrip()
        {
            DisconnectMessage msg = new DisconnectMessage(DisconnectReason.AlreadyConnected);
            DisconnectMessageSerializer serializer = new DisconnectMessageSerializer();
            byte[] serialized = serializer.Serialize(msg);
            Assert.AreEqual("0xc105", serialized.ToHexString(true), "bytes");
            DisconnectMessage deserialized = serializer.Deserialize(serialized);
            Assert.AreEqual(msg.Reason, deserialized.Reason, "reason");
        }

        [Test]
        public void Can_read_single_byte_message()
        {
            DisconnectMessageSerializer serializer = new DisconnectMessageSerializer();
            byte[] serialized = new byte[] {16};
            DisconnectMessage deserialized = serializer.Deserialize(serialized);
            Assert.AreEqual(DisconnectReason.Other, (DisconnectReason)deserialized.Reason, "reason");
        }
        
        // does this format happen more often?
//        [Test]
//        public void Can_read_other_format_message()
//        {
//            DisconnectMessageSerializer serializer = new DisconnectMessageSerializer();
//            byte[] serialized = Bytes.FromHexString("0204c108");
//            DisconnectMessage deserialized = serializer.Deserialize(serialized);
//            Assert.AreEqual(DisconnectReason.Other, (DisconnectReason)deserialized.Reason, "reason");
//        }
    }
}
