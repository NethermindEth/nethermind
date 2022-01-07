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

using System;
using DotNetty.Buffers;
using FluentAssertions;
using FluentAssertions.Equivalency;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P
{
    public static class SerializerTester
    {
        public static void Test<T>(IMessageSerializer<T> serializer, T message, string expectedData = null) where T : P2PMessage
        {
            byte[] serialized = serializer.Serialize(message);
            T deserialized = serializer.Deserialize(serialized);
            deserialized.Should().BeEquivalentTo(message);
            byte[] serializedAgain = serializer.Serialize(deserialized);
            Assert.AreEqual(serialized.ToHexString(), serializedAgain.ToHexString(), "test old way");

            if (expectedData != null)
            {
                Assert.AreEqual(expectedData, serialized.ToHexString());
            }
        }

        public static void TestZero<T>(IZeroMessageSerializer<T> serializer, T message, string expectedData = null) where T : P2PMessage
        {
            IByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(1024 * 16);
            IByteBuffer buffer2 = PooledByteBufferAllocator.Default.Buffer(1024 * 16);
            try
            {
                serializer.Serialize(buffer, message);
                T deserialized = serializer.Deserialize(buffer);
                
                // RlpLength is calculated explicitly when serializing an object by Calculate method. It's null after deserialization.
                deserialized.Should().BeEquivalentTo(message, options => options.Excluding(c => c.Name == "RlpLength"));
                
                Assert.AreEqual(0, buffer.ReadableBytes, "readable bytes");
                
                serializer.Serialize(buffer2, deserialized);

                buffer.SetReaderIndex(0);
                string allHex = buffer.ReadAllHex();
                Assert.AreEqual(allHex, buffer2.ReadAllHex(), "test zero");
                
                if (expectedData != null)
                {
                    allHex.Should().BeEquivalentTo(expectedData);
                }
            }
            finally
            {
                buffer.Release();
                buffer2.Release();
            }
        }
    }
}
