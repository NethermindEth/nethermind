// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System;
using System.Linq.Expressions;
using DotNetty.Buffers;
using FluentAssertions;
using FluentAssertions.Equivalency;
using Nethermind.Network.P2P.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P
{
    public static class SerializerTester
    {
        public static void TestZero<T>(IZeroMessageSerializer<T> serializer, T message, string? expectedData = null, Func<EquivalencyAssertionOptions<T>, EquivalencyAssertionOptions<T>>? additionallyExcluding = null) where T : P2PMessage
        {
            IByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(1024 * 16);
            IByteBuffer buffer2 = PooledByteBufferAllocator.Default.Buffer(1024 * 16);
            try
            {
                serializer.Serialize(buffer, message);
                T deserialized = serializer.Deserialize(buffer);

                // RlpLength is calculated explicitly when serializing an object by Calculate method. It's null after deserialization.
                deserialized.Should().BeEquivalentTo(message, options =>
                {
                    EquivalencyAssertionOptions<T>? excluded = options.Excluding(c => c.Name == "RlpLength");
                    return additionallyExcluding is not null ? additionallyExcluding(excluded) : excluded;
                });

                Assert.AreEqual(0, buffer.ReadableBytes, "readable bytes");

                serializer.Serialize(buffer2, deserialized);

                buffer.SetReaderIndex(0);
                string allHex = buffer.ReadAllHex();
                Assert.AreEqual(allHex, buffer2.ReadAllHex(), "test zero");

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
