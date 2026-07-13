// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class SlabArenaTests
{
    [Test]
    public void Append_and_read_round_trip_with_and_without_keccak()
    {
        SlabArena arena = new();
        SlabArena.Cursor cursor = SlabArena.Cursor.Empty;
        byte[] rlp = [1, 2, 3, 4, 5];

        SlabHandle withKeccak = arena.Append(ref cursor, TestItem.KeccakA, rlp, SlabFlags.None);
        SlabHandle withoutKeccak = arena.Append(ref cursor, null, rlp, SlabFlags.None);
        SlabHandle emptyUnknown = arena.Append(ref cursor, TestItem.KeccakB, default, SlabFlags.EmptyUnknown);

        int generation = arena.Generation;
        Assert.That(arena.TryReadCopy(withKeccak, generation, out Hash256? k1, out byte[]? r1), Is.True);
        Assert.That(k1, Is.EqualTo(TestItem.KeccakA));
        Assert.That(r1, Is.EqualTo(rlp));

        Assert.That(arena.TryReadCopy(withoutKeccak, generation, out Hash256? k2, out byte[]? r2), Is.True);
        Assert.That(k2, Is.Null);
        Assert.That(r2, Is.EqualTo(rlp));

        Assert.That(arena.TryReadCopy(emptyUnknown, generation, out Hash256? k3, out byte[]? r3), Is.True);
        Assert.That(k3, Is.EqualTo(TestItem.KeccakB));
        Assert.That(r3, Is.Null);
        Assert.That((emptyUnknown.Flags & SlabFlags.EmptyUnknown) != 0, Is.True);
    }

    [Test]
    public void First_record_is_padded_so_none_handle_is_never_produced()
    {
        SlabArena arena = new();
        SlabArena.Cursor cursor = SlabArena.Cursor.Empty;
        SlabHandle first = arena.Append(ref cursor, null, new byte[] { 42 }, SlabFlags.None);
        Assert.That(first.IsNone, Is.False);
        Assert.That(first.Offset, Is.EqualTo(1));
    }

    [Test]
    public void Rolls_to_a_new_slab_at_the_boundary()
    {
        SlabArena arena = new();
        SlabArena.Cursor cursor = SlabArena.Cursor.Empty;
        byte[] big = new byte[SlabHandle.MaxRlpLen];
        int perSlab = SlabArena.SlabSize / (32 + big.Length);
        SlabHandle last = default;
        for (int i = 0; i < perSlab + 2; i++) last = arena.Append(ref cursor, TestItem.KeccakA, big, SlabFlags.None);
        Assert.That(last.SlabIndex, Is.GreaterThanOrEqualTo(1));
        Assert.That(arena.BytesReserved, Is.GreaterThanOrEqualTo(2 * SlabArena.SlabSize));

        int generation = arena.Generation;
        Assert.That(arena.TryReadCopy(last, generation, out _, out byte[]? rlp), Is.True);
        Assert.That(rlp!.Length, Is.EqualTo(big.Length));
    }

    [Test]
    public void Read_with_stale_generation_after_release_is_a_miss()
    {
        SlabArena arena = new();
        SlabArena.Cursor cursor = SlabArena.Cursor.Empty;
        SlabHandle handle = arena.Append(ref cursor, TestItem.KeccakA, new byte[] { 9, 9 }, SlabFlags.None);
        int staleGeneration = arena.Generation;

        arena.Release();

        Assert.That(arena.TryReadCopy(handle, staleGeneration, out _, out _), Is.False);
    }

    [Test]
    public void Oversize_rlp_throws()
    {
        SlabArena arena = new();
        SlabArena.Cursor cursor = SlabArena.Cursor.Empty;
        byte[] oversize = new byte[SlabHandle.MaxRlpLen + 1];
        Assert.That(() => arena.Append(ref cursor, null, oversize, SlabFlags.None), Throws.InvalidOperationException);
    }

    [Test]
    public void Release_resets_counters_and_reuse_works()
    {
        SlabArena arena = new();
        SlabArena.Cursor cursor = SlabArena.Cursor.Empty;
        arena.Append(ref cursor, null, new byte[] { 1 }, SlabFlags.None);
        Assert.That(arena.BytesAppended, Is.GreaterThan(0));

        arena.Release();
        Assert.That(arena.BytesAppended, Is.Zero);
        Assert.That(arena.BytesReserved, Is.Zero);

        cursor = SlabArena.Cursor.Empty;
        SlabHandle again = arena.Append(ref cursor, null, new byte[] { 2 }, SlabFlags.None);
        Assert.That(arena.TryReadCopy(again, arena.Generation, out _, out byte[]? rlp), Is.True);
        Assert.That(rlp, Is.EqualTo(new byte[] { 2 }));
    }

    [Test]
    public void AppendShared_round_trips_and_pads_first_record()
    {
        SlabArena arena = new();
        long cursor = SlabArena.EmptySharedCursor;
        byte[] rlp = [7, 7, 7];

        SlabHandle first = arena.AppendShared(ref cursor, TestItem.KeccakA, rlp, SlabFlags.None);
        Assert.That(first.IsNone, Is.False);
        Assert.That(first.Offset, Is.EqualTo(1));

        Assert.That(arena.TryReadCopy(first, arena.Generation, out Hash256? keccak, out byte[]? readRlp), Is.True);
        Assert.That(keccak, Is.EqualTo(TestItem.KeccakA));
        Assert.That(readRlp, Is.EqualTo(rlp));
    }

    [Test]
    public void AppendShared_cursors_grab_distinct_slabs()
    {
        SlabArena arena = new();
        long cursorA = SlabArena.EmptySharedCursor;
        long cursorB = SlabArena.EmptySharedCursor;

        SlabHandle a = arena.AppendShared(ref cursorA, null, new byte[] { 1 }, SlabFlags.None);
        SlabHandle b = arena.AppendShared(ref cursorB, null, new byte[] { 2 }, SlabFlags.None);

        Assert.That(a.SlabIndex, Is.Not.EqualTo(b.SlabIndex), "each shared cursor must own its slab exclusively");
        Assert.That(arena.TryReadCopy(a, arena.Generation, out _, out byte[]? ra), Is.True);
        Assert.That(arena.TryReadCopy(b, arena.Generation, out _, out byte[]? rb), Is.True);
        Assert.That(ra, Is.EqualTo(new byte[] { 1 }));
        Assert.That(rb, Is.EqualTo(new byte[] { 2 }));
    }

    [Test]
    public void AppendShared_is_safe_under_concurrent_appends()
    {
        SlabArena arena = new();
        const int shards = 8;
        const int perShard = 2000;
        long[] cursors = new long[shards];
        for (int i = 0; i < shards; i++) cursors[i] = SlabArena.EmptySharedCursor;

        ConcurrentBag<(SlabHandle Handle, byte Value)> handles = [];
        Parallel.For(0, shards, s =>
        {
            for (int i = 0; i < perShard; i++)
            {
                byte value = (byte)((s * 31 + i) & 0xFF);
                SlabHandle handle = arena.AppendShared(ref cursors[s], null, new[] { value }, SlabFlags.None);
                handles.Add((handle, value));
            }
        });

        int generation = arena.Generation;
        foreach ((SlabHandle handle, byte value) in handles)
        {
            Assert.That(arena.TryReadCopy(handle, generation, out _, out byte[]? rlp), Is.True);
            Assert.That(rlp, Is.EqualTo(new[] { value }));
        }
    }
}
