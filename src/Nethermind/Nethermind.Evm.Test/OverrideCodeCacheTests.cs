// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class OverrideCodeCacheTests
{
    [Test]
    public void Same_bytes_in_another_array_reuse_the_hash_and_the_code_info()
    {
        byte[] first = [0x60, 0x01, 0x60, 0x02, 0x01, 0x5b];
        byte[] second = (byte[])first.Clone();

        OverrideCodeCache.Resolve(first, out ValueHash256 firstHash, out CodeInfo firstInfo);
        OverrideCodeCache.Resolve(second, out ValueHash256 secondHash, out CodeInfo secondInfo);

        Assert.That(firstHash, Is.EqualTo(ValueKeccak.Compute(first)));
        Assert.That(secondHash, Is.EqualTo(firstHash));
        Assert.That(secondInfo, Is.SameAs(firstInfo));
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
    public void Empty_code_has_the_empty_hash()
    {
        OverrideCodeCache.Resolve([], out ValueHash256 hash, out CodeInfo info);
        Assert.That(hash, Is.EqualTo(ValueKeccak.OfAnEmptyString));
        Assert.That(info.IsEmpty, Is.True);
    }
}
