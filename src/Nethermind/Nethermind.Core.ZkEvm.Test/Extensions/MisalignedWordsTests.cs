// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.ZkEvm.Test.Extensions;

/// <summary>
/// Covers <see cref="Bytes.WriteMisalignedWords"/>, <see cref="Bytes.ReadMisalignedWords"/> and
/// <see cref="Bytes.SequenceEqualByWords"/>, which only the strict-alignment guest calls, so no host path reaches them.
/// </summary>
public class MisalignedWordsTests
{
    [Test]
    public unsafe void Reads_the_words([Range(1, 7)] int skew)
    {
        byte[] buffer = new byte[5 * sizeof(ulong)];
        for (int i = 0; i < buffer.Length; i++) buffer[i] = (byte)(i + 1);
        ulong[] words = new ulong[4];

        fixed (byte* source = buffer)
        fixed (ulong* destination = words)
        {
            Bytes.ReadMisalignedWords(source + skew, destination);
        }

        byte[] read = new byte[32];
        Buffer.BlockCopy(words, 0, read, 0, read.Length);
        Assert.That(read, Is.EqualTo(buffer.AsSpan(skew, 32).ToArray()));
    }

    [Test]
    public void Compares_by_words([Range(0, 7)] int skewA, [Range(0, 7)] int skewB, [Values(0, 1, 7, 8, 9, 15, 16, 17, 31, 32)] int length)
    {
        byte[] a = new byte[length + 2 * sizeof(ulong)];
        byte[] b = new byte[length + 2 * sizeof(ulong)];
        Array.Fill(a, (byte)0xaa);
        Array.Fill(b, (byte)0xbb);
        for (int i = 0; i < length; i++) a[skewA + i] = b[skewB + i] = (byte)(i * 7 + 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Bytes.SequenceEqualByWords(a.AsSpan(skewA, length), b.AsSpan(skewB, length)), Is.True);
            Assert.That(Bytes.SequenceEqualByWords(a.AsSpan(skewA, length), b.AsSpan(skewB, Math.Max(0, length - 1))), Is.EqualTo(length == 0));
            for (int i = 0; i < length; i++)
            {
                b[skewB + i] ^= 0x10;
                Assert.That(Bytes.SequenceEqualByWords(a.AsSpan(skewA, length), b.AsSpan(skewB, length)), Is.False, $"byte {i}");
                b[skewB + i] ^= 0x10;
            }
        }
    }

    [Test]
    public unsafe void Writes_the_words_and_keeps_the_bytes_around_them([Range(1, 7)] int skew)
    {
        ulong[] words = [0x0807_0605_0403_0201UL, 0x100f_0e0d_0c0b_0a09UL, 0x1817_1615_1413_1211UL, 0x201f_1e1d_1c1b_1a19UL];
        byte[] value = new byte[32];
        Buffer.BlockCopy(words, 0, value, 0, value.Length);
        byte[] buffer = new byte[5 * sizeof(ulong)];
        Array.Fill(buffer, (byte)0xee);
        byte[] expected = (byte[])buffer.Clone();
        value.CopyTo(expected, skew);

        fixed (byte* destination = buffer)
        fixed (ulong* source = words)
        {
            Bytes.WriteMisalignedWords(destination + skew, source);
        }

        Assert.That(buffer, Is.EqualTo(expected));
    }
}
