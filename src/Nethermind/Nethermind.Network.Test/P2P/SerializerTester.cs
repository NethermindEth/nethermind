// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P
{
    public static class SerializerTester
    {
        public static void TestZero<T>(IZeroMessageSerializer<T> serializer, T message, string? expectedData = null) where T : P2PMessage
        {
            try
            {
                int length = serializer.GetLength(message, out _);
                using PooledBuffer buffer = PooledBuffer.Rent(length);
                serializer.Serialize(buffer.Span, message);
                using T deserialized = serializer.Deserialize(buffer.ReadOnlySpan, out int consumed);

                Assert.That(deserialized, Is.Not.Null);

                Assert.That(consumed, Is.EqualTo(length), "consumed bytes");

                using PooledBuffer buffer2 = PooledBuffer.Rent(serializer.GetLength(deserialized, out _));
                serializer.Serialize(buffer2.Span, deserialized);

                string allHex = buffer.ReadOnlySpan.ToHexString();
                Assert.That(buffer2.ReadOnlySpan.ToHexString(), Is.EqualTo(allHex), "test zero");

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
