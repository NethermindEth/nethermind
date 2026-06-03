// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P
{
    public static class SerializerTester
    {
        public static void TestZero<T>(IZeroMessageSerializer<T> serializer, T message, string? expectedData = null) where T : P2PMessage
        {
            using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(1024 * 16).AsDisposable();
            using DisposableByteBuffer buffer2 = PooledByteBufferAllocator.Default.Buffer(1024 * 16).AsDisposable();
            try
            {
                serializer.Serialize(buffer, message);
                using T deserialized = serializer.Deserialize(buffer);

                Assert.That(deserialized, Is.Not.Null);

                Assert.That(buffer.ReadableBytes, Is.EqualTo(0), "readable bytes");

                serializer.Serialize(buffer2, deserialized);

                buffer.SetReaderIndex(0);
                string allHex = buffer.ReadAllHex();
                Assert.That(buffer2.ReadAllHex(), Is.EqualTo(allHex), "test zero");

                if (expectedData is not null)
                {
                    Assert.That(allHex, Is.EqualTo(expectedData));
                }
            }
            finally
            {
                message.TryDispose();
            }
        }
    }
}
