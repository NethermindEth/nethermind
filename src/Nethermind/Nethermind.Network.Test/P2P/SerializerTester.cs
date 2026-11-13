// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P
{
    public static class SerializerTester
    {
        public static void Test<T>(IMessageSerializer<T> serializer, T message, string? expectedData = null) where T : P2PMessage
        {
            try
            {
                string serialized = serializer.SerializeToArray(message).ToHexString();
                using T deserialized = serializer.Deserialize(Bytes.FromHexString(serialized));

                Assert.That(serializer.SerializeToArray(deserialized).ToHexString(), Is.EqualTo(serialized), "round trip");

                if (expectedData is not null)
                {
                    Assert.That(serialized, Is.EqualTo(expectedData));
                }
            }
            finally
            {
                message.TryDispose();
            }
        }
    }
}
