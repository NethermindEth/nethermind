// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using NUnit.Framework;

namespace Nethermind.Core.ZkEvm.Test;

/// <summary>
/// Covers the guest form of <see cref="Bloom.ReadIndexBits"/>. <see cref="Bloom.Set(ReadOnlySpan{byte}, Bloom)"/> cannot
/// reach it on the host, because it hashes through the zkVM keccak syscall.
/// </summary>
public class GuestBloomIndexBitsTests
{
    [Test]
    public void Reads_the_first_hash_word_big_endian([Range(0, 7)] int offset)
    {
        byte[] buffer = new byte[16];
        new Random(offset).NextBytes(buffer);

        Assert.That(Bloom.ReadIndexBits(ref buffer[offset]), Is.EqualTo(BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(offset))));
    }
}
