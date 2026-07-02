// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Persistence;

/// <summary>
/// Regression tests for <c>BaseTriePersistence.WriteBatch.DeleteStateTrieNodeRange</c> and
/// <c>DeleteStorageTrieNodeRange</c>. Specifically targets the FallbackNodes column where the
/// length byte is at a fixed offset after the path bytes — the bug being that a 64-encoded
/// firstKey length byte caused entries with the same path bytes but smaller length to sort
/// below firstKey and escape deletion.
/// </summary>
[TestFixture]
public class BaseTriePersistenceDeletionTests
{
    private SnapshotableMemColumnsDb<FlatDbColumns> _columnsDb = null!;
    private IPersistence _persistence = null!;

    [SetUp]
    public void SetUp()
    {
        _columnsDb = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _persistence = new RocksDbPersistence(_columnsDb);
    }

    [TearDown]
    public void TearDown() => _columnsDb.Dispose();

    /// <summary>
    /// State FallbackNodes layout: <c>[0x00][path(32)][length(1)]</c>. A node at the
    /// <em>lower bound</em> of a deletion range (path bytes match fromPath exactly) but with
    /// length &lt; 64 must still be deleted. Previously firstKey encoded length=64, so byte 33
    /// sorted such an entry below firstKey and `GetViewBetween` skipped it.
    /// </summary>
    [Test]
    public void DeleteStateTrieNodeRange_removes_FallbackNodes_at_range_lower_bound_with_shorter_length()
    {
        // Write a state-trie FallbackNodes entry at path bytes [0x12, 0...], length 20 (within the
        // "0x12*" subtree we're about to clear) — bypassing the high-level API so we exactly
        // exercise the persistence-layer encoding.
        byte[] key = new byte[34];
        key[0] = 0x00;
        key[1] = 0x12;
        // bytes 2..32 stay zero
        key[33] = 20;
        IDb fallback = _columnsDb.GetColumnDb(FlatDbColumns.FallbackNodes);
        fallback.Set(key, new byte[] { 0xAA });

        // Range delete over the "0x12*" subtree (fromPath = 0x12000...0 len 64, toPath = 0x12FFF...F len 64).
        ValueHash256 from = ValueKeccak.Zero;
        from.BytesAsSpan[0] = 0x12;
        ValueHash256 to = ValueKeccak.Zero;
        to.BytesAsSpan.Fill(0xFF);
        to.BytesAsSpan[0] = 0x12;

        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL))
        {
            batch.DeleteStateTrieNodeRange(new TreePath(from, 64), new TreePath(to, 64));
        }

        fallback.KeyExists(key).Should().BeFalse(
            "the entry at path 0x12 / length 20 lies inside the 0x12* deletion range and must be cleared");
    }

    /// <summary>
    /// Symmetric case for the storage FallbackNodes section (prefix 0x01, layout
    /// <c>[0x01][addr_pre(4)][path(32)][length(1)][addr_suf(16)]</c>). The address suffix
    /// filter in <c>DeleteMatchingKeys</c> rejects entries for other addresses; we only care
    /// about our address's entry surviving the buggy lower bound.
    /// </summary>
    [Test]
    public void DeleteStorageTrieNodeRange_removes_FallbackNodes_at_range_lower_bound_with_shorter_length()
    {
        // Pick an arbitrary address — content doesn't matter, just need a stable hash.
        ValueHash256 addressHash = ValueKeccak.Compute(new byte[] { 1, 2, 3 });

        // Storage FallbackNodes key layout: [0x01][addr_prefix(4)][path(32)][length(1)][addr_suffix(16)]
        byte[] key = new byte[54];
        key[0] = 0x01;
        addressHash.Bytes[..4].CopyTo(key.AsSpan(1, 4));
        key[5] = 0x34;                                   // path byte 0 → first nibble pair "34"
        // bytes 6..36 stay zero
        key[37] = 25;                                    // length
        addressHash.Bytes[4..20].CopyTo(key.AsSpan(38, 16));

        IDb fallback = _columnsDb.GetColumnDb(FlatDbColumns.FallbackNodes);
        fallback.Set(key, new byte[] { 0xBB });

        // Range delete over the "0x34*" subtree for this address.
        ValueHash256 from = ValueKeccak.Zero;
        from.BytesAsSpan[0] = 0x34;
        ValueHash256 to = ValueKeccak.Zero;
        to.BytesAsSpan.Fill(0xFF);
        to.BytesAsSpan[0] = 0x34;

        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL))
        {
            batch.DeleteStorageTrieNodeRange(addressHash, new TreePath(from, 64), new TreePath(to, 64));
        }

        fallback.KeyExists(key).Should().BeFalse(
            "the storage entry at addr/path 0x34 / length 25 lies inside the 0x34* deletion range and must be cleared");
    }

    /// <summary>
    /// Counter-test: an entry whose path is OUTSIDE the deletion range must survive, regardless
    /// of length. Guards against the fix accidentally widening the range too far.
    /// </summary>
    [Test]
    public void DeleteStateTrieNodeRange_does_not_remove_FallbackNodes_outside_the_range()
    {
        // Survivor at path bytes [0xAB, 0...], length 30 — outside the "0x12*" range we delete.
        byte[] survivor = new byte[34];
        survivor[0] = 0x00;
        survivor[1] = 0xAB;
        survivor[33] = 30;
        IDb fallback = _columnsDb.GetColumnDb(FlatDbColumns.FallbackNodes);
        fallback.Set(survivor, new byte[] { 0xCC });

        ValueHash256 from = ValueKeccak.Zero;
        from.BytesAsSpan[0] = 0x12;
        ValueHash256 to = ValueKeccak.Zero;
        to.BytesAsSpan.Fill(0xFF);
        to.BytesAsSpan[0] = 0x12;

        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL))
        {
            batch.DeleteStateTrieNodeRange(new TreePath(from, 64), new TreePath(to, 64));
        }

        fallback.KeyExists(survivor).Should().BeTrue(
            "the entry at path 0xAB is outside the 0x12* deletion range and must not be cleared");
    }
}
