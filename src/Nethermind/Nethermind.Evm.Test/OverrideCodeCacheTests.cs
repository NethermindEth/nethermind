// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Serialization.Json;
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
            using (Assert.EnterMultipleScope())
            {
                Assert.That(firstHash, Is.EqualTo(ValueKeccak.Compute(first)));
                Assert.That(secondHash, Is.EqualTo(firstHash));
            }
            reused = ReferenceEquals(secondInfo, firstInfo);
        }

        Assert.That(reused, Is.True);
    }

    [Test]
    public void Codes_that_share_a_set_do_not_evict_each_other()
    {
        byte[] prefix = Guid.NewGuid().ToByteArray();
        List<byte[]> codes = [];
        for (int i = 0; codes.Count < OverrideCodeCache.Ways; i++)
        {
            byte[] code = [.. prefix, (byte)i, (byte)(i >> 8), (byte)(i >> 16)];
            if (codes.Count == 0 || OverrideCodeCache.SetOf(code) == OverrideCodeCache.SetOf(codes[0])) codes.Add(code);
        }

        // As above, a parallel fixture can replace an entry between the two passes.
        bool reused = false;
        for (int attempt = 0; attempt < 5 && !reused; attempt++)
        {
            CodeInfo[] first = [.. codes.Select(Resolve)];
            reused = codes.Select(Resolve).SequenceEqual(first);
        }

        Assert.That(reused, Is.True);

        static CodeInfo Resolve(byte[] code)
        {
            OverrideCodeCache.Resolve(code, out _, out CodeInfo info);
            return info;
        }
    }

    [Test]
    public void A_repeated_override_code_is_read_as_the_cached_array([Values] bool withPrefix)
    {
        byte[] code = [0x60, 0x01, 0x5b, .. Guid.NewGuid().ToByteArray()];
        string json = $$"""{"code":"{{code.ToHexString(withPrefix)}}"}""";

        bool reused = false;
        for (int attempt = 0; attempt < 5 && !reused; attempt++)
        {
            OverrideCodeCache.Resolve(code, out _, out _);
            reused = ReferenceEquals(JsonSerializer.Deserialize<AccountOverride>(json, EthereumJsonSerializer.JsonOptions)!.Code, code);
        }

        Assert.That(reused, Is.True);
    }

    [Test]
    public void An_override_code_not_seen_before_is_read_into_its_own_array()
    {
        byte[] code = [0x60, 0x02, 0x5b, .. Guid.NewGuid().ToByteArray()];

        AccountOverride read = JsonSerializer.Deserialize<AccountOverride>($$"""{"code":"{{code.ToHexString(true)}}"}""", EthereumJsonSerializer.JsonOptions)!;

        Assert.That(read.Code, Is.EqualTo(code).And.Not.SameAs(code));
    }

    [Test]
    public void Different_bytes_never_share_an_entry()
    {
        for (int i = 0; i < 2000; i++)
        {
            byte[] code = [0x60, (byte)i, 0x60, (byte)(i >> 8), 0x01];
            OverrideCodeCache.Resolve(code, out ValueHash256 hash, out CodeInfo info);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(hash, Is.EqualTo(ValueKeccak.Compute(code)), $"code {i}");
                Assert.That(info.CodeSpan.SequenceEqual(code), Is.True, $"code {i}");
            }
        }
    }

    [Test]
    public void A_miss_keeps_the_callers_array_rather_than_a_copy()
    {
        byte[] code = [0x60, 0x2a, 0x60, 0x00, 0x52, 0x5b, .. Guid.NewGuid().ToByteArray()];
        OverrideCodeCache.Resolve(code, out _, out CodeInfo info);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(MemoryMarshal.TryGetArray(info.Code, out ArraySegment<byte> segment), Is.True);
            Assert.That(segment.Array, Is.SameAs(code));
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hash, Is.EqualTo(ValueKeccak.OfAnEmptyString));
            Assert.That(info.IsEmpty, Is.True);
        }
    }
}
