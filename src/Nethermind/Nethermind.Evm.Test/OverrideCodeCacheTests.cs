// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class OverrideCodeCacheTests
{
    private const int Ways = OverrideCodeCache.Ways;
    private const int Sets = OverrideCodeCache.Sets;

    [Test]
    public void Same_bytes_in_another_array_reuse_the_hash_and_the_code_info()
    {
        // Distinct bytes per run: the cache is process-wide and other fixtures run in parallel, so a lookup can
        // occasionally land in a set other tests just filled; a few tries rule that out.
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
    public void A_miss_keeps_the_callers_array_rather_than_a_copy()
    {
        byte[] code = [0x60, 0x2a, 0x60, 0x00, 0x52, 0x5b, .. Guid.NewGuid().ToByteArray()];
        OverrideCodeCache.Resolve(code, out _, out CodeInfo info);

        Assert.That(MemoryMarshal.TryGetArray(info.Code, out ArraySegment<byte> segment), Is.True);
        Assert.That(segment.Array, Is.SameAs(code));
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

    [Test]
    public void Two_codes_with_the_same_hash_both_stay_cached()
    {
        // The sizes of the two codes that met in one slot of the old direct-mapped table on every request.
        OverrideCodeCache cache = new();
        byte[] large = RandomCode(15_132, seed: 1);
        byte[] small = RandomCode(1_201, seed: 2);
        const int sameHash = 0x5a5a;

        cache.Get(large, sameHash, out _, out CodeInfo largeInfo);
        cache.Get(small, sameHash, out _, out CodeInfo smallInfo);

        for (int request = 0; request < 100; request++)
        {
            cache.Get((byte[])large.Clone(), sameHash, out ValueHash256 largeHash, out CodeInfo largeAgain);
            cache.Get((byte[])small.Clone(), sameHash, out ValueHash256 smallHash, out CodeInfo smallAgain);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(largeAgain, Is.SameAs(largeInfo), $"request {request}");
                Assert.That(smallAgain, Is.SameAs(smallInfo), $"request {request}");
                Assert.That(largeHash, Is.EqualTo(ValueKeccak.Compute(large)));
                Assert.That(smallHash, Is.EqualTo(ValueKeccak.Compute(small)));
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(largeInfo.CodeSpan.SequenceEqual(large), Is.True);
            Assert.That(smallInfo.CodeSpan.SequenceEqual(small), Is.True);
            Assert.That(cache.Count, Is.EqualTo(2));
        }
    }

    [Test]
    public void Codes_of_the_same_hash_and_length_get_their_own_entries()
    {
        OverrideCodeCache cache = new();
        byte[] first = RandomCode(100, seed: 11);
        byte[] second = RandomCode(100, seed: 12);
        const int sameHash = 0x1234;

        cache.Get(first, sameHash, out _, out CodeInfo firstInfo);
        for (int request = 0; request < 3; request++)
        {
            cache.Get((byte[])second.Clone(), sameHash, out ValueHash256 secondHash, out CodeInfo secondInfo);
            cache.Get((byte[])first.Clone(), sameHash, out ValueHash256 firstHash, out CodeInfo firstAgain);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(secondHash, Is.EqualTo(ValueKeccak.Compute(second)), $"request {request}");
                Assert.That(secondInfo.CodeSpan.SequenceEqual(second), Is.True, $"request {request}");
                Assert.That(firstHash, Is.EqualTo(ValueKeccak.Compute(first)), $"request {request}");
                Assert.That(firstAgain, Is.SameAs(firstInfo), $"request {request}");
            }
        }

        Assert.That(cache.Count, Is.EqualTo(2));
    }

    [Test]
    public void A_full_set_drops_the_code_added_first_even_if_it_was_just_used()
    {
        OverrideCodeCache cache = new();
        const int set = 3;
        byte[][] codes = Enumerable.Range(0, Ways + 2).Select(static i => RandomCode(64, seed: 100 + i)).ToArray();
        // Different hashes that pick the same set.
        static int HashOf(int i) => set + i * Sets;

        for (int i = 0; i < Ways; i++) cache.Get(codes[i], HashOf(i), out _, out _);
        for (int i = 0; i < Ways; i++) Assert.That(cache.Contains(codes[i], HashOf(i)), Is.True, $"code {i} before the set is full");

        // A hit does not make the first code newer: adding to the full set still drops it.
        cache.Get(codes[0], HashOf(0), out _, out _);
        cache.Get(codes[Ways], HashOf(Ways), out _, out _);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.Contains(codes[0], HashOf(0)), Is.False, "code 0 after one more");
            for (int i = 1; i <= Ways; i++) Assert.That(cache.Contains(codes[i], HashOf(i)), Is.True, $"code {i} after one more");
        }

        cache.Get(codes[Ways + 1], HashOf(Ways + 1), out _, out _);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.Contains(codes[1], HashOf(1)), Is.False, "code 1 after two more");
            for (int i = 2; i <= Ways + 1; i++) Assert.That(cache.Contains(codes[i], HashOf(i)), Is.True, $"code {i} after two more");
            Assert.That(cache.Count, Is.EqualTo(Ways));
        }
    }

    [Test]
    public void A_full_set_leaves_the_other_sets_alone()
    {
        OverrideCodeCache cache = new();
        byte[] neighbour = RandomCode(64, seed: 7);
        cache.Get(neighbour, 4, out _, out _);

        for (int i = 0; i < 3 * Ways; i++) cache.Get(RandomCode(64, seed: 200 + i), 5 + i * Sets, out _, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.Contains(neighbour, 4), Is.True);
            Assert.That(cache.Count, Is.EqualTo(1 + Ways));
        }
    }

    [Test]
    public void The_cache_never_holds_more_than_its_capacity()
    {
        OverrideCodeCache cache = new();
        for (int i = 0; i < 10 * Sets * Ways; i++) cache.Get(RandomCode(40, seed: i), out _, out _);
        Assert.That(cache.Count, Is.LessThanOrEqualTo(Sets * Ways));

        // One more code than a set holds in every set.
        for (int i = 0; i < (Ways + 1) * Sets; i++) cache.Get(RandomCode(40, seed: 50_000 + i), i, out _, out _);
        Assert.That(cache.Count, Is.EqualTo(Sets * Ways));
    }

    [Test]
    public void Concurrent_lookups_of_codes_crowding_one_set_get_their_own_hash()
    {
        // Twice as many codes as a set holds, all in one set, so lookups race additions and evictions.
        OverrideCodeCache cache = new();
        byte[][] codes = Enumerable.Range(0, 2 * Ways).Select(static i => RandomCode(256 + i, seed: 300 + i)).ToArray();
        ValueHash256[] hashes = codes.Select(static code => ValueKeccak.Compute(code)).ToArray();

        Parallel.For(0, 50_000, i =>
        {
            int k = i % codes.Length;
            cache.Get(codes[k], 9, out ValueHash256 hash, out CodeInfo info);
            if (hash != hashes[k] || !info.CodeSpan.SequenceEqual(codes[k]))
                throw new InvalidOperationException($"wrong entry for code {k}");
        });

        Assert.That(cache.Count, Is.LessThanOrEqualTo(Ways));
    }

    [Test]
    public void Racing_misses_of_one_code_store_it_once()
    {
        OverrideCodeCache cache = new();
        byte[] code = RandomCode(15_132, seed: 400);
        const int threadCount = 8;
        CodeInfo[] infos = new CodeInfo[threadCount];
        using Barrier start = new(threadCount);

        Thread[] threads = Enumerable.Range(0, threadCount).Select(t => new Thread(() =>
        {
            byte[] copy = (byte[])code.Clone();
            start.SignalAndWait();
            cache.Get(copy, 11, out _, out infos[t]);
        })).ToArray();
        foreach (Thread thread in threads) thread.Start();
        foreach (Thread thread in threads) thread.Join();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(infos, Is.All.SameAs(infos[0]));
            Assert.That(cache.Count, Is.EqualTo(1));
        }
    }

    private static byte[] RandomCode(int length, int seed)
    {
        byte[] code = new byte[length];
        new Random(seed).NextBytes(code);
        return code;
    }
}
