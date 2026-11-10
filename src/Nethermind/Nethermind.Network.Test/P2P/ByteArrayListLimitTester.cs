// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P
{
    /// <summary>
    /// Shared assertions for a response message carrying an RLP byte-string list that is capped on decode.
    /// </summary>
    /// <remarks>
    /// A list at the cap must decode with its full item count, and one item above it must be rejected.
    /// Either way the packet buffer must not stay retained: the decoder takes a lease on it, so a
    /// missed release would pin a pooled buffer per message until finalization.
    /// </remarks>
    internal static class ByteArrayListLimitTester
    {
        /// <summary>
        /// Yields the at-cap and one-above-cap cases for <paramref name="limit"/>, read from the limit
        /// actually wired into the serializer so the boundary cannot drift away from it.
        /// </summary>
        public static IEnumerable<TestCaseData> BoundaryCases(RlpLimit limit)
        {
            yield return new TestCaseData(limit.Limit, false).SetName("{m}(at limit)");
            yield return new TestCaseData(limit.Limit + 1, true).SetName("{m}(above limit)");
        }

        /// <remarks>Decodes through <see cref="MessageSerializationService"/>, which hands the packet buffer to an <see cref="IOwningMessageSerializer{T}"/>.</remarks>
        public static void AssertLimitEnforced<TMessage>(
            IMessageSerializer<TMessage> serializer,
            Func<IByteArrayList, TMessage> createMessage,
            Func<TMessage, int> itemCount,
            int items,
            bool shouldThrow)
            where TMessage : P2PMessage
        {
            MessageSerializationService service = new(SerializerInfo.Create(serializer));
            AssertLimitEnforced(serializer.Serialize, service.Deserialize<TMessage>, createMessage, itemCount, items, shouldThrow);
        }

        private static void AssertLimitEnforced<TMessage>(
            Action<IByteBuffer, TMessage> serialize,
            Func<IByteBuffer, TMessage> deserialize,
            Func<IByteArrayList, TMessage> createMessage,
            Func<TMessage, int> itemCount,
            int items,
            bool shouldThrow)
            where TMessage : P2PMessage
        {
            ArrayPoolList<byte[]> entries = new(items);
            for (int i = 0; i < items; i++)
            {
                entries.Add([0x42]);
            }

            using TMessage message = createMessage(new ByteArrayListAdapter(entries));

            using DisposableByteBuffer buffer = UnpooledByteBufferAllocator.Default.Buffer(items + 64).AsDisposable();
            serialize(buffer, message);
            int referenceCountBeforeDecode = buffer.ReferenceCount;

            if (shouldThrow)
            {
                Assert.Throws<RlpLimitException>(() => deserialize(buffer));
            }
            else
            {
                DecodeAndAssertItemCount(deserialize, buffer, itemCount, items);
            }

            Assert.That(buffer.ReferenceCount, Is.EqualTo(referenceCountBeforeDecode), "packet buffer must not stay retained");

            // Scoped so the message is disposed before the reference count is read.
            static void DecodeAndAssertItemCount(
                Func<IByteBuffer, TMessage> deserialize,
                IByteBuffer buffer,
                Func<TMessage, int> itemCount,
                int items)
            {
                using TMessage deserialized = deserialize(buffer);
                Assert.That(itemCount(deserialized), Is.EqualTo(items));
            }
        }
    }
}
