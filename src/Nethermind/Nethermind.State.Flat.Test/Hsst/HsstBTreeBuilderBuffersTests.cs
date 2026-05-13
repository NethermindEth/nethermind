// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.State.Flat.Hsst;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class HsstBTreeBuilderBuffersTests
{
    /// <summary>
    /// Two builds with identical inputs must produce identical HSST bytes regardless of
    /// whether each build allocated its own work buffers (the auto-owned constructor)
    /// or shared a single <see cref="HsstBTreeBuilderBuffers"/> across both builds.
    ///
    /// The shared-buffers path also runs two consecutive builds against one struct so
    /// the second build exercises buffer reuse (cleared lists, re-rented arrays).
    /// </summary>
    [TestCase(2, 1)]
    [TestCase(2, 8)]
    [TestCase(2, 256)]
    [TestCase(4, 8)]
    [TestCase(4, 4096)]
    [TestCase(30, 8)]
    [TestCase(33, 256)]
    public void Reused_buffers_produce_identical_output(int keyLength, int entryCount)
    {
        (byte[] Key, byte[] Value)[] entries = MakeEntries(keyLength, entryCount, seed: 0xBEEFu);

        byte[] auto1 = HsstTestUtil.BuildToArray(buildAction: BuildAll, keyLength: keyLength);
        byte[] auto2 = HsstTestUtil.BuildToArray(buildAction: BuildAll, keyLength: keyLength);

        // Sanity: deterministic across runs of the auto-owned path.
        Assert.That(auto2, Is.EqualTo(auto1));

        // Shared-buffers path — two consecutive builds against one buffers struct.
        // The second build is the one that actually exercises buffer reuse.
        HsstBTreeBuilderBuffers buffers = new();
        try
        {
            byte[] shared1 = BuildWithBuffers(ref buffers, keyLength, entries);
            byte[] shared2 = BuildWithBuffers(ref buffers, keyLength, entries);

            Assert.That(shared1, Is.EqualTo(auto1), "first shared-buffers build must match auto-owned build");
            Assert.That(shared2, Is.EqualTo(auto1), "reused-buffers build must match auto-owned build");
        }
        finally
        {
            buffers.Dispose();
        }

        void BuildAll(ref HsstBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder)
        {
            foreach ((byte[] k, byte[] v) in entries) builder.Add(k, v);
        }
    }

    private static byte[] BuildWithBuffers(scoped ref HsstBTreeBuilderBuffers buffers, int keyLength, (byte[] Key, byte[] Value)[] entries)
    {
        using PooledByteBufferWriter pooled = new(10 * 1024 * 1024);
        HsstBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder =
            new(ref pooled.GetWriter(), ref buffers, keyLength);
        try
        {
            foreach ((byte[] k, byte[] v) in entries) builder.Add(k, v);
            builder.Build();
            return pooled.WrittenSpan.ToArray();
        }
        finally
        {
            builder.Dispose();
        }
    }

    /// <summary>
    /// Synthetic sorted key/value pairs. Keys are derived from the seed via a simple
    /// xorshift so the test is deterministic; we sort after generation to satisfy
    /// the HSST builder's sorted-input contract.
    /// </summary>
    private static (byte[] Key, byte[] Value)[] MakeEntries(int keyLength, int count, uint seed)
    {
        (byte[] Key, byte[] Value)[] entries = new (byte[], byte[])[count];
        uint state = seed;
        for (int i = 0; i < count; i++)
        {
            byte[] key = new byte[keyLength];
            for (int j = 0; j < keyLength; j++)
            {
                state ^= state << 13; state ^= state >> 17; state ^= state << 5;
                key[j] = (byte)state;
            }
            byte[] value = new byte[(int)((state % 16u) + 1u)];
            for (int j = 0; j < value.Length; j++)
            {
                state ^= state << 13; state ^= state >> 17; state ^= state << 5;
                value[j] = (byte)state;
            }
            entries[i] = (key, value);
        }
        Array.Sort(entries, static (a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));
        // Drop duplicates (sorted input must be strictly increasing for the builder).
        int write = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            if (write == 0 || entries[i].Key.AsSpan().SequenceCompareTo(entries[write - 1].Key) > 0)
            {
                entries[write++] = entries[i];
            }
        }
        if (write != entries.Length) Array.Resize(ref entries, write);
        return entries;
    }
}
