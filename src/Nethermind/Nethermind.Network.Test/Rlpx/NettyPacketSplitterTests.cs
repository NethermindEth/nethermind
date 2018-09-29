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

using System.Collections.Generic;
using DotNetty.Transport.Channels;
using Nethermind.Network.Rlpx;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx
{
    [TestFixture]
    public class NettyPacketSplitterTests
    {
        private class UnderTest : NettyPacketSplitter
        {
            private readonly IChannelHandlerContext _context = Substitute.For<IChannelHandlerContext>();

            public void Encode(Packet message, List<object> output)
            {
                base.Encode(_context, message, output);
            }
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void Splits_packet_into_frames(int framesCount)
        {
            Packet packet = new Packet("eth", 2, new byte[(framesCount - 1) * NettyPacketSplitter.FrameBoundary * 64 + 1]);
            List<object> output = new List<object>();

            UnderTest underTest = new UnderTest();
            underTest.Encode(packet, output);

            Assert.AreEqual(framesCount, output.Count);
        }

        [Test]
        public void Splits_packet_into_two_frames()
        {
            Packet packet = new Packet("eth", 2, new byte[NettyPacketSplitter.FrameBoundary * 64 + 1]);
            List<object> output = new List<object>();

            UnderTest underTest = new UnderTest();
            underTest.Encode(packet, output);

            Assert.AreEqual(2, output.Count);
        }
        
        [Test]
        public void Padding_is_done_after_adding_packet_size()
        {
            Packet packet = new Packet("eth", 2, new byte[NettyPacketSplitter.FrameBoundary * 64 - 1]);
            List<object> output = new List<object>();

            UnderTest underTest = new UnderTest();
            underTest.Encode(packet, output);

            Assert.AreEqual(1, output.Count);
        }
    }
}