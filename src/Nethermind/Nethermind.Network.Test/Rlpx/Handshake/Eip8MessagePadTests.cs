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

using Nethermind.Network.Rlpx.Handshake;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx.Handshake
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class Eip8MessagePadTests
    {
        [Test]
        public void Adds_at_least_100_bytes()
        {
            byte[] message = {1};
            int lengthBeforePadding = message.Length;

            TestRandom testRandom = new TestRandom(i => 0, i => new byte[i]);

            Eip8MessagePad pad = new Eip8MessagePad(testRandom);
            message = pad.Pad(message);

            Assert.AreEqual(lengthBeforePadding + 100, message.Length, "incorrect length");
            Assert.AreEqual(message[0], 1, "first byte touched");
        }

        [Test]
        public void Adds_at_most_300_bytes()
        {
            byte[] message = {1};
            int lengthBeforePadding = message.Length;

            TestRandom testRandom = new TestRandom(i => i - 1, i => new byte[i]);

            Eip8MessagePad pad = new Eip8MessagePad(testRandom);
            message = pad.Pad(message);

            Assert.AreEqual(lengthBeforePadding + 300, message.Length, "incorrect length");
            Assert.AreEqual(message[0], 1, "first byte touched");
        }
    }
}
