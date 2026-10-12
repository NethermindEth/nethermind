// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Rust.Interop;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>How readers, iterators, batches and the store of <see cref="RustPersistence"/> outlive each other, and the metrics they publish.</summary>
[Parallelizable(ParallelScope.All)]
public class RustPersistenceLifecycleTests
{
    private static readonly Address Address = new("0xaaaaaaaa00000000000000000000000000000001");
    private static readonly TreePath Path = TreePath.FromHexString("123");
    private static readonly StateId Block1 = new(1, Keccak.Compute("1"));
    private static readonly StateId Block2 = new(2, Keccak.Compute("2"));
    private static readonly ValueHash256 MaxKey = new("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");

    [Test]
    public void Readers_and_iterators_keep_reading_their_snapshot_after_later_batches([Values] FlatTestDbs.Kind dbKind)
    {
        RustNativeLibrary.Require();
        if (dbKind == FlatTestDbs.Kind.Memory)
        {
            // Probable bug in SnapshotableMemDb: it tracks live snapshots in a set of versions, so disposing one of two
            // snapshots of the same version (here the batch's own) unprotects the other, and the next write prunes the
            // versions that snapshot still reads.
            Assert.Ignore("SnapshotableMemDb prunes versions a live snapshot reads once another snapshot of its version is disposed");
        }

        using TempPath path = TempPath.GetTempDirectory();
        using IColumnsDb<FlatDbColumns> db = FlatTestDbs.Open(dbKind, path.Path);
        using RustPersistence persistence = new(db, new FlatDbConfig(), LimboLogs.Instance);
        Write(persistence, StateId.PreGenesis, Block1, 1);

        IPersistence.IPersistenceReader reader = persistence.CreateReader();
        IPersistence.IFlatIterator iterator = reader.CreateAccountIterator(ValueKeccak.Zero, MaxKey);
        Write(persistence, Block1, Block2, 2);
        reader.Dispose();

        using IPersistence.IPersistenceReader latest = persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(iterator.MoveNext(), Is.True, "the iterator outlives its reader");
            Assert.That(iterator.MoveNext(), Is.False);
            Assert.That(latest.CurrentState, Is.EqualTo(Block2));
            Assert.That(latest.GetAccount(Address)?.Nonce, Is.EqualTo(2));
        }

        iterator.Dispose();

        // Opened before the second batch, it still reads the first.
        IPersistence.IPersistenceReader stale = persistence.CreateReader(ReaderFlags.Sync);
        Write(persistence, Block2, new StateId(3, Keccak.Compute("3")), 3);
        using (stale)
        {
            Assert.That(stale.CurrentState, Is.EqualTo(Block2));
            Assert.That(stale.GetAccount(Address)?.Nonce, Is.EqualTo(2));
            Assert.That(stale.TryLoadStateRlp(Path, ReadFlags.None), Is.EqualTo(Rlp(2)));
        }
    }

    [Test]
    public void Disposing_the_persistence_waits_for_its_open_handles([Values] FlatTestDbs.Kind dbKind)
    {
        RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        using IColumnsDb<FlatDbColumns> db = FlatTestDbs.Open(dbKind, path.Path);
        RustPersistence persistence = new(db, new FlatDbConfig(), LimboLogs.Instance);
        Write(persistence, StateId.PreGenesis, Block1, 1);
        RustFlatStore store = persistence.Store;

        IPersistence.IPersistenceReader reader = persistence.CreateReader();
        IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(Block1, Block2);
        batch.SetAccount(Address, new Account(2, 2));
        FdbStatus destroyWithHandlesOpen = NativeMethods.fdb_store_destroy(store.DangerousGetHandle());
        persistence.Dispose();
        ulong? nonce = reader.GetAccount(Address)?.Nonce;
        bool closedWithHandlesOpen = store.IsClosed;
        batch.Dispose();
        reader.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(destroyWithHandlesOpen, Is.EqualTo(FdbStatus.InvalidArgument), "the native store refuses to go with handles open");
            Assert.That(nonce, Is.EqualTo(1), "an open reader keeps the store");
            Assert.That(closedWithHandlesOpen, Is.False);
            Assert.That(store.IsClosed, Is.True, "the last handle released the store");
            Assert.That(() => persistence.CreateReader(), Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(store.Bridge?.OpenHandles ?? 0, Is.Zero, "the destroyed store released every snapshot and iterator");
        }

        using RustPersistence reopened = new(db, new FlatDbConfig(), LimboLogs.Instance);
        using IPersistence.IPersistenceReader committed = reopened.CreateReader();
        Assert.That(committed.CurrentState, Is.EqualTo(Block2), "the batch committed after the persistence was disposed");
    }

    [Test]
    [NonParallelizable]
    public void Reads_and_carry_forward_counts_reach_the_csharp_metrics()
    {
        RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        using FlatTestDbs.TrackingColumnsDb db = new(path.Path);
        using RustPersistence persistence = new(db, new FlatDbConfig(), LimboLogs.Instance);
        Write(persistence, StateId.PreGenesis, Block1, 1);
        bool detailedMetrics = Nethermind.Db.Metrics.DetailedMetricsEnabled;
        Nethermind.Db.Metrics.DetailedMetricsEnabled = true;
        try
        {
            long totalReads = db.GatherMetric().TotalReads;
            long accountMisses = Metrics.CarryForwardAccountMisses;
            long accountHits = Metrics.CarryForwardAccountHits;
            long slotMisses = Metrics.CarryForwardSlotMisses;

            IPersistence.IPersistenceReader reader = persistence.CreateReader();
            UInt256 slot = default;
            reader.GetAccount(Address);
            reader.GetAccount(Address);
            reader.TryGetSlot(Address, 1, ref slot);
            reader.TryLoadStateRlp(Path, ReadFlags.None);
            long unpublished = db.GatherMetric().TotalReads;
            reader.Dispose();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(unpublished, Is.EqualTo(totalReads), "reads are published when the reader is disposed");
                Assert.That(db.GatherMetric().TotalReads - totalReads, Is.EqualTo(2), "the account miss and the slot read; the hit and trie reads are not counted");
                Assert.That(Metrics.CarryForwardAccountMisses - accountMisses, Is.EqualTo(1));
                Assert.That(Metrics.CarryForwardAccountHits - accountHits, Is.EqualTo(1));
                Assert.That(Metrics.CarryForwardSlotMisses - slotMisses, Is.EqualTo(1));
                Assert.That(Metrics.CarryForwardAccountCount, Is.EqualTo(1));
                Assert.That(Metrics.CarryForwardSlotCount, Is.EqualTo(1));
            }
        }
        finally
        {
            Nethermind.Db.Metrics.DetailedMetricsEnabled = detailedMetrics;
        }
    }

    private static byte[] Rlp(ulong value) => [0xc1, (byte)value];

    private static void Write(IPersistence persistence, StateId from, StateId to, ulong value)
    {
        using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(from, to);
        batch.SetAccount(Address, new Account(value, value));
        batch.SetStorage(Address, 1, value);
        batch.SetStateTrieNode(Path, Rlp(value));
    }
}
