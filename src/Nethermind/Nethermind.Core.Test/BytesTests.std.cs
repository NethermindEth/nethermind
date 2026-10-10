// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public partial class BytesTests
{
    [TestCase(false, TestName = "FastHash_StructuredEightByteInputs_AreDistributed_PublicPath")]
    [TestCase(true, TestName = "FastHash_StructuredEightByteInputs_AreDistributed_ScalarFallback")]
    public void FastHash_StructuredEightByteInputs_AreDistributed(bool forceScalar)
    {
        const int count = 1024;
        ulong pairedDelta = SolveCrcInput(0);
        byte[] input0 = new byte[sizeof(ulong)];
        byte[] input1 = new byte[sizeof(ulong)];
        int equalPairs = 0;

        foreach (uint seed in HashSeeds)
            Assert.That(SpanExtensions.CombineHash(seed, pairedDelta),
                Is.EqualTo(SpanExtensions.CombineHash(seed, 0)), "raw CRC collision survives changing the seed");

        for (uint value = 0; value < count; value++)
        {
            ulong word = value * 0x9E3779B97F4A7C15UL;
            BinaryPrimitives.WriteUInt64LittleEndian(input0, word);
            BinaryPrimitives.WriteUInt64LittleEndian(input1, word ^ pairedDelta);

            int hash0 = forceScalar
                ? SpanExtensions.FastHashFallback(input0)
                : input0.FastHash();
            int hash1 = forceScalar
                ? SpanExtensions.FastHashFallback(input1)
                : input1.FastHash();
            if (SpanExtensions.CombineHash(0, (uint)hash0) == SpanExtensions.CombineHash(0, (uint)hash1)) equalPairs++;
        }

        Assert.That(equalPairs, Is.LessThan(2), $"structured pairs produced {equalPairs}/{count} equal chained hashes");
    }
}
