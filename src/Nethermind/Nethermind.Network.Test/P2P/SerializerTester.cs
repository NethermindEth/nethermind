// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System;
using DotNetty.Buffers;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P
{
    public static class SerializerTester
    {
        public static void TestZero<T>(IZeroMessageSerializer<T> serializer, T message, string expectedData = null) where T : P2PMessage
        {
            IByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(1024 * 16);
            IByteBuffer buffer2 = PooledByteBufferAllocator.Default.Buffer(1024 * 16);
            try
            {
                serializer.Serialize(buffer, message);
                T deserialized = serializer.Deserialize(buffer);

                // RlpLength is calculated explicitly when serializing an object by Calculate method. It's null after deserialization.
                deserialized.Should().BeEquivalentTo(message, options => options
                    .Excluding(c => c.Name == "RlpLength")
                    .Using<Memory<byte>>((context => context.Subject.FasterToArray().Should().BeEquivalentTo(context.Expectation.FasterToArray())))
                    .WhenTypeIs<Memory<byte>>()
                );

                Assert.That(buffer.ReadableBytes, Is.EqualTo(0), "readable bytes");

                serializer.Serialize(buffer2, deserialized);

                buffer.SetReaderIndex(0);
                string allHex = buffer.ReadAllHex();
                Assert.That(buffer2.ReadAllHex(), Is.EqualTo(allHex), "test zero");

                if (expectedData is not null)
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
