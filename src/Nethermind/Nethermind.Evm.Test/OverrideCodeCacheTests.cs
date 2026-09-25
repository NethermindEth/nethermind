// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class OverrideCodeCacheTests
{
    [Test]
    public void Same_bytes_in_another_array_reuse_the_hash_and_the_code_info()
    {
        // Distinct bytes per run: the cache is process-wide and other fixtures run in parallel, so a lookup can
        // occasionally land in a slot another test just replaced; a few tries rule that out.
        byte[] first = [0x60, 0x01, 0x60, 0x02, 0x01, 0x5b, .. Guid.NewGuid().ToByteArray()];
        byte[] second = (byte[])first.Clone();

        bool reused = false;
        for (int attempt = 0; attempt < 5 && !reused; attempt++)
        {
            OverrideCodeCache.Resolve(first, out ValueHash256 firstHash, out CodeInfo firstInfo);
            OverrideCodeCache.Resolve(second, out ValueHash256 secondHash, out CodeInfo secondInfo);
            Assert.That(firstHash, Is.EqualTo(ValueKeccak.Compute(first)));
            Assert.That(secondHash, Is.EqualTo(firstHash));
            reused = ReferenceEquals(secondInfo, firstInfo);
        }

        Assert.That(reused, Is.True);
    }

    [Test]
    public void Different_bytes_never_share_an_entry()
    {
        for (int i = 0; i < 2000; i++)
        {
            byte[] code = [0x60, (byte)i, 0x60, (byte)(i >> 8), 0x01];
            OverrideCodeCache.Resolve(code, out ValueHash256 hash, out CodeInfo info);
            Assert.That(hash, Is.EqualTo(ValueKeccak.Compute(code)), $"code {i}");
            Assert.That(info.CodeSpan.SequenceEqual(code), Is.True, $"code {i}");
        }
    }

    [Test]
    public void Changing_the_callers_array_later_never_returns_the_old_hash()
    {
        byte[] code = [0x60, 0x2a, 0x60, 0x00, 0x52, 0x5b, 0x00];
        OverrideCodeCache.Resolve(code, out _, out _);

        code[1] = 0x2b;
        OverrideCodeCache.Resolve(code, out ValueHash256 hash, out CodeInfo info);

        Assert.That(hash, Is.EqualTo(ValueKeccak.Compute(code)));
        Assert.That(info.CodeSpan.SequenceEqual(code), Is.True);
    }

    [Test]
    public void Concurrent_lookups_always_get_the_hash_of_their_own_bytes()
    {
        byte[][] codes = new byte[64][];
        for (int i = 0; i < codes.Length; i++) codes[i] = [0x60, (byte)i, 0x60, (byte)(i * 7), 0x02, 0x5b];

        Parallel.For(0, 20_000, i =>
        {
            byte[] code = codes[i % codes.Length];
            OverrideCodeCache.Resolve(code, out ValueHash256 hash, out CodeInfo info);
            if (hash != ValueKeccak.Compute(code) || !info.CodeSpan.SequenceEqual(code))
                throw new InvalidOperationException($"wrong entry for code {i % codes.Length}");
        });
    }

    [Test]
    public void Empty_code_has_the_empty_hash()
    {
        OverrideCodeCache.Resolve([], out ValueHash256 hash, out CodeInfo info);
        Assert.That(hash, Is.EqualTo(ValueKeccak.OfAnEmptyString));
        Assert.That(info.IsEmpty, Is.True);
    }
}
