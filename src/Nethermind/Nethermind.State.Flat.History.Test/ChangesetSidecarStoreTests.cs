// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Db;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class ChangesetSidecarStoreTests
{
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _columnsDb = null!;
    private ChangesetSidecarStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _columnsDb = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _store = new ChangesetSidecarStore(_columnsDb.GetColumnDb(FlatHistoryColumns.ChangesetSidecar));
    }

    [TearDown]
    public void TearDown() => _columnsDb.Dispose();

    [Test]
    public void RecordChangeset_SmallerThanCap_WritesASingleChunk()
    {
        byte[] payload = Fill(1024);

        Write(1, payload);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_store.TryGetChunk(1, 0), Is.EqualTo(payload));
            Assert.That(_store.TryGetChunk(1, 1), Is.Null, "a payload under the cap must not spill into a second chunk");
        }
    }

    [Test]
    public void RecordChangeset_ExactlyAtTheCap_WritesASingleChunk()
    {
        byte[] payload = Fill(ChangesetSidecarStore.MaxChunkPayloadBytes);

        Write(1, payload);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_store.TryGetChunk(1, 0), Is.EqualTo(payload));
            Assert.That(_store.TryGetChunk(1, 1), Is.Null, "a payload of exactly the cap must still fit in one chunk, not spill an empty second one");
        }
    }

    [Test]
    public void RecordChangeset_OneByteOverTheCap_SplitsIntoTwoChunks()
    {
        byte[] payload = Fill(ChangesetSidecarStore.MaxChunkPayloadBytes + 1);

        Write(1, payload);

        byte[]? chunk0 = _store.TryGetChunk(1, 0);
        byte[]? chunk1 = _store.TryGetChunk(1, 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chunk0, Is.Not.Null);
            Assert.That(chunk0!.Length, Is.EqualTo(ChangesetSidecarStore.MaxChunkPayloadBytes));
            Assert.That(chunk1, Is.Not.Null);
            Assert.That(chunk1!.Length, Is.EqualTo(1));
            Assert.That(_store.TryGetChunk(1, 2), Is.Null);
            AssertReassembles(payload, chunk0, chunk1);
        }
    }

    // A destruct-heavy block's changeset spans 3+ chunks: chunk index must stay contiguous and 0-based, and
    // concatenating every chunk back together must reproduce the original payload exactly.
    [Test]
    public void RecordChangeset_SpanningThreeOrMoreChunks_ReassemblesExactly()
    {
        byte[] payload = Fill(2 * ChangesetSidecarStore.MaxChunkPayloadBytes + 500);

        Write(7, payload);

        byte[]? chunk0 = _store.TryGetChunk(7, 0);
        byte[]? chunk1 = _store.TryGetChunk(7, 1);
        byte[]? chunk2 = _store.TryGetChunk(7, 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chunk0, Is.Not.Null);
            Assert.That(chunk0!.Length, Is.EqualTo(ChangesetSidecarStore.MaxChunkPayloadBytes));
            Assert.That(chunk1, Is.Not.Null);
            Assert.That(chunk1!.Length, Is.EqualTo(ChangesetSidecarStore.MaxChunkPayloadBytes));
            Assert.That(chunk2, Is.Not.Null);
            Assert.That(chunk2!.Length, Is.EqualTo(500));
            Assert.That(_store.TryGetChunk(7, 3), Is.Null, "the sequence must end exactly where the payload ends");
            AssertReassembles(payload, chunk0, chunk1, chunk2);
        }
    }

    [Test]
    public void RecordChangeset_EmptyPayload_StillWritesChunkZero()
    {
        Write(1, ReadOnlySpan<byte>.Empty);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_store.TryGetChunk(1, 0), Is.EqualTo(Array.Empty<byte>()),
                "an empty changeset must still be distinguishable from a block that was never recorded");
            Assert.That(_store.TryGetChunk(1, 1), Is.Null);
        }
    }

    [Test]
    public void RecordChangeset_DifferentBlocks_DoNotBleedIntoEachOthersChunks()
    {
        byte[] payloadA = Fill(ChangesetSidecarStore.MaxChunkPayloadBytes + 10, seed: 1);
        byte[] payloadB = Fill(ChangesetSidecarStore.MaxChunkPayloadBytes + 20, seed: 2);

        Write(10, payloadA);
        Write(20, payloadB);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_store.TryGetChunk(10, 0)!.Length, Is.EqualTo(ChangesetSidecarStore.MaxChunkPayloadBytes));
            Assert.That(_store.TryGetChunk(10, 1)!.Length, Is.EqualTo(10));
            Assert.That(_store.TryGetChunk(20, 0)!.Length, Is.EqualTo(ChangesetSidecarStore.MaxChunkPayloadBytes));
            Assert.That(_store.TryGetChunk(20, 1)!.Length, Is.EqualTo(20));
        }
    }

    private void Write(ulong block, ReadOnlySpan<byte> payload)
    {
        using IColumnsWriteBatch<FlatHistoryColumns> batch = _columnsDb.StartWriteBatch();
        _store.RecordChangeset(block, payload, batch.GetColumnBatch(FlatHistoryColumns.ChangesetSidecar));
    }

    private static void AssertReassembles(byte[] expected, params byte[]?[] chunks)
    {
        byte[] reassembled = new byte[expected.Length];
        int offset = 0;
        foreach (byte[]? chunk in chunks)
        {
            chunk!.CopyTo(reassembled, offset);
            offset += chunk.Length;
        }

        Assert.That(reassembled, Is.EqualTo(expected));
    }

    private static byte[] Fill(int length, int seed = 0)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i + seed);
        }

        return bytes;
    }
}
