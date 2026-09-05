// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.ZkEvm.Test.Extensions;

/// <summary>
/// Bucket-window distribution for the guest's scalar hash mixers, which the host suite cannot reach:
/// <see cref="SpanExtensions.FastHash64For20Bytes"/> picks the AES path whenever the hardware has AES,
/// so these fallbacks stay unexercised even in a ZK_EVM build running on x64.
/// </summary>
/// <remarks>
/// Windows and thresholds mirror <c>AssertHash64WindowsAreDistributed</c> in
/// <c>Nethermind.Core.Test/BytesTests.cs</c>, because these hashes end up in the same bucketed caches.
/// The counter sweep matters more than any single offset: the lane multiplies carry upward only, so a
/// key whose entropy sits in the high half of a lane -- offset 4, 12, 20 or 28 -- is the shape that
/// starves the low output bits.
/// </remarks>
public class GuestMixerTests
{
    private const int SampleCount = 4096;

    /// <remarks>The guest installs its hash seed at start-up rather than in a static initializer, which
    /// is what keeps a class-initialisation check off every mixer call; a test process reaches these
    /// mixers without going through <c>StatelessExecutor.Execute</c>, so it has to do the same.</remarks>
    [OneTimeSetUp]
    public void SeedHashes() => SpanExtensions.SeedHashes(SpanExtensions.DefaultHashSeed);

    public static IEnumerable<TestCaseData> CounterOffsets()
    {
        foreach (int length in new[] { 20, 32 })
        {
            for (int offset = 0; offset + sizeof(uint) <= length; offset += sizeof(uint))
            {
                yield return new TestCaseData(length, offset).SetName(
                    $"Guest_mixer_distributes_{length}_byte_keys_with_entropy_at_offset_{offset}");
            }
        }
    }

    [TestCaseSource(nameof(CounterOffsets))]
    public void Guest_mixer_distributes_bucket_windows(int length, int offset)
    {
        byte[] input = new byte[length];
        long[] hashes = new long[SampleCount];

        for (uint value = 0; value < SampleCount; value++)
        {
            input.AsSpan().Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(offset), value);
            ref byte start = ref MemoryMarshal.GetArrayDataReference(input);
            hashes[value] = length == 20
                ? SpanExtensions.FastHash64For20BytesFallback(ref start)
                : SpanExtensions.FastHash64For32BytesFallback(ref start);
        }

        AssertWindowsAreDistributed(hashes, $"{length}-byte keys, entropy at offset {offset}");
    }

    /// <remarks>
    /// The tail read of a 20-byte key is the zero-extension of the same bytes in a 32-byte one, and
    /// the unused lane contributes nothing, so before the lane multipliers were seeded per width the
    /// two forms mixed to an identical value. <c>FastHash_ShortPaddingIncludesLength</c> in the host
    /// suite names this property but exercises 8 against 9 bytes, which both take the CRC path.
    /// </remarks>
    [Test]
    public void Guest_mixer_separates_an_address_from_its_zero_padded_word()
    {
        byte[] address = new byte[20];
        byte[] padded = new byte[32];
        for (int i = 0; i < address.Length; i++)
        {
            address[i] = (byte)(0xA0 + i);
            padded[i] = address[i];
        }

        long addressHash = SpanExtensions.FastHash64For20BytesFallback(
            ref MemoryMarshal.GetArrayDataReference(address));
        long paddedHash = SpanExtensions.FastHash64For32BytesFallback(
            ref MemoryMarshal.GetArrayDataReference(padded));

        Assert.That(addressHash, Is.Not.EqualTo(paddedHash));
    }

    private static void AssertWindowsAreDistributed(long[] hashes, string context)
    {
        HashSet<long> fullHashes = new(hashes.Length);
        HashSet<int> way0Sets = new(hashes.Length);
        HashSet<int> signatures = new(hashes.Length);
        HashSet<int> way1Sets = new(hashes.Length);

        foreach (long hash in hashes)
        {
            ulong bits = (ulong)hash;
            fullHashes.Add(hash);
            way0Sets.Add((int)(bits & 0x3FFF));
            signatures.Add((int)((bits >> 22) & 0xF_FFFF));
            way1Sets.Add((int)((bits >> 42) & 0x3FFF));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fullHashes.Count, Is.GreaterThan(hashes.Length - 32), $"{context}: full hash");
            Assert.That(way0Sets.Count, Is.GreaterThan(hashes.Length * 3 / 4), $"{context}: bits 0-13");
            Assert.That(signatures.Count, Is.GreaterThan(hashes.Length - 32), $"{context}: bits 22-41");
            Assert.That(way1Sets.Count, Is.GreaterThan(hashes.Length * 3 / 4), $"{context}: bits 42-55");
        }
    }
}
