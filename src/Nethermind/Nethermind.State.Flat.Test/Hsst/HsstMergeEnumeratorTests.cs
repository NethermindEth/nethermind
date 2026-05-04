// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using Nethermind.State.Flat.Hsst;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class HsstMergeEnumeratorTests
{
    [TestCase("common_prefix_", 12)]
    [TestCase("longer_shared_prefix_", 8)]
    [TestCase("", 6)] // empty-prefix regression guard
    [TestCase("p_", 5)]
    public void Enumerate_InlineMode_KeysWithCommonPrefix_YieldsFullKeys(string prefix, int count)
    {
        List<(byte[] Key, byte[] Value)> entries = new(count);
        for (int i = 0; i < count; i++)
        {
            entries.Add((Encoding.UTF8.GetBytes($"{prefix}{i:D3}"), Encoding.UTF8.GetBytes($"v{i:D3}")));
        }

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer> builder) =>
        {
            foreach ((byte[] key, byte[] value) in entries)
                builder.Add(key, value);
        }, maxLeafEntries: 64, inlineValues: true);

        ReadOnlySpan<byte> hsstData = data.AsSpan();

        using HsstMergeEnumerator e = new(hsstData, isInline: true);

        int idx = 0;
        while (e.MoveNext(hsstData))
        {
            Assert.That(e.CurrentKey.SequenceEqual(entries[idx].Key), Is.True,
                $"Key mismatch at idx {idx}. Expected {Encoding.UTF8.GetString(entries[idx].Key)}, got {Encoding.UTF8.GetString(e.CurrentKey)}");
            Assert.That(e.GetCurrentValue(hsstData).SequenceEqual(entries[idx].Value), Is.True,
                $"Value mismatch at idx {idx}");
            idx++;
        }
        Assert.That(idx, Is.EqualTo(count));
    }

    [Test]
    public void Enumerate_InlineMode_TwoStreamsWithCommonPrefix_MergeKeysAreFullKeys()
    {
        // Two HSSTs with overlapping common-prefixed keys — emulates the inputs to
        // PersistedSnapshotBuilder.NWayNestedStreamingMerge in inline mode.
        const string prefix = "shared_prefix_";
        List<(byte[] Key, byte[] Value)> a = new();
        List<(byte[] Key, byte[] Value)> b = new();
        for (int i = 0; i < 10; i++)
        {
            a.Add((Encoding.UTF8.GetBytes($"{prefix}{i:D3}_A"), Encoding.UTF8.GetBytes($"av{i:D3}")));
            b.Add((Encoding.UTF8.GetBytes($"{prefix}{i:D3}_B"), Encoding.UTF8.GetBytes($"bv{i:D3}")));
        }

        byte[] dataA = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer> builder) =>
        {
            foreach ((byte[] k, byte[] v) in a) builder.Add(k, v);
        }, maxLeafEntries: 64, inlineValues: true);

        byte[] dataB = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer> builder) =>
        {
            foreach ((byte[] k, byte[] v) in b) builder.Add(k, v);
        }, maxLeafEntries: 64, inlineValues: true);

        ReadOnlySpan<byte> spanA = dataA.AsSpan();
        ReadOnlySpan<byte> spanB = dataB.AsSpan();

        using HsstMergeEnumerator eA = new(spanA, isInline: true);
        using HsstMergeEnumerator eB = new(spanB, isInline: true);

        bool hasA = eA.MoveNext(spanA);
        bool hasB = eB.MoveNext(spanB);
        int ai = 0, bi = 0;
        while (hasA || hasB)
        {
            int cmp = (hasA, hasB) switch
            {
                (true, false) => -1,
                (false, true) => 1,
                _ => eA.CurrentKey.SequenceCompareTo(eB.CurrentKey),
            };
            if (cmp <= 0)
            {
                Assert.That(eA.CurrentKey.SequenceEqual(a[ai].Key), Is.True,
                    $"A-stream key mismatch at ai={ai}. Expected {Encoding.UTF8.GetString(a[ai].Key)}, got {Encoding.UTF8.GetString(eA.CurrentKey)}");
                ai++;
                hasA = eA.MoveNext(spanA);
            }
            else
            {
                Assert.That(eB.CurrentKey.SequenceEqual(b[bi].Key), Is.True,
                    $"B-stream key mismatch at bi={bi}. Expected {Encoding.UTF8.GetString(b[bi].Key)}, got {Encoding.UTF8.GetString(eB.CurrentKey)}");
                bi++;
                hasB = eB.MoveNext(spanB);
            }
        }
        Assert.That(ai, Is.EqualTo(a.Count));
        Assert.That(bi, Is.EqualTo(b.Count));
    }
}
