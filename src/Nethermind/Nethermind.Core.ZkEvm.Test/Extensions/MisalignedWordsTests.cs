// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.ZkEvm.Test.Extensions;

/// <summary>
/// Covers <see cref="Bytes.WriteMisalignedWords"/>, which only the strict-alignment guest calls, so no host path
/// reaches it.
/// </summary>
public class MisalignedWordsTests
{
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
