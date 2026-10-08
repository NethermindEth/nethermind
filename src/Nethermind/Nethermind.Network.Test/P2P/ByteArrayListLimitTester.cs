// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Buffers;
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
    /// Decoding borrows the rented packet span, while messages that retain decoded content take
    /// ownership of their own copy, so releasing the packet right after decode is always safe.
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

        public static void AssertLimitEnforced<TMessage>(
            IZeroMessageSerializer<TMessage> serializer,
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

            using PooledBuffer buffer = PooledBuffer.Rent(serializer.GetLength(message, out _));
            serializer.Serialize(buffer.Span, message);

            if (shouldThrow)
            {
                Assert.Throws<RlpLimitException>(() => serializer.Deserialize(buffer.ReadOnlySpan, out _));
            }
            else
            {
                DecodeAndAssertItemCount(serializer, buffer.ReadOnlySpan, itemCount, items);
            }

            // Scoped so the message is disposed before the packet buffer is released.
            static void DecodeAndAssertItemCount(
                IZeroMessageSerializer<TMessage> serializer,
                ReadOnlySpan<byte> data,
                Func<TMessage, int> itemCount,
                int items)
            {
                using TMessage deserialized = serializer.Deserialize(data, out _);
                Assert.That(itemCount(deserialized), Is.EqualTo(items));
            }
        }
    }
}
