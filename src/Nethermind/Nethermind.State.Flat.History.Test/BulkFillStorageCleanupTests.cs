// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.State.Flat.History.Changesets;
using NUnit.Framework;
using Columns = Nethermind.State.Flat.History.Changesets.BulkFillScratchState.Columns;

namespace Nethermind.State.Flat.History.Test;

/// <summary>
/// After each replayed block the session drops the slots that a storage clear made stale, and the clear markers with
/// them, whether the cleanup scans the whole clears column or cleans only the accounts the block cleared.
/// </summary>
public class BulkFillStorageCleanupTests
{
    private static readonly Account Contract = Build.An.Account.WithBalance(1).TestObject;

    [Test]
    public void CleanStorage_drops_the_stale_slots_and_markers_of_each_block([Values] bool reopenBeforeTheClear)
    {
        using Fixture fixture = new();
        fixture.Replay(writer => SetSlots(writer, TestItem.AddressA, 1, 2));
        fixture.Session.CleanStorage(CancellationToken.None);
        if (reopenBeforeTheClear) fixture.Reopen();

        fixture.Replay(writer =>
        {
            ClearAndSetSlots(writer, TestItem.AddressA, 3);
            ClearAndSetSlots(writer, TestItem.AddressB, 1);
            ClearAndSetSlots(writer, TestItem.AddressB, 2);
        });
        fixture.Session.CleanStorage(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Count(Columns.Clears), Is.Zero);
            Assert.That(fixture.Count(Columns.Storage), Is.EqualTo(3), "slot 3 of A and slots 1 and 2 of B remain");
        }
    }

    [Test]
    public void CleanStorage_after_a_restart_drops_the_markers_a_stopped_run_left()
    {
        using Fixture fixture = new();
        fixture.Replay(writer => SetSlots(writer, TestItem.AddressA, 1, 2));
        fixture.Session.CleanStorage(CancellationToken.None);
        // The run stops after committing a block with a clear and before cleaning it.
        fixture.Replay(writer => ClearAndSetSlots(writer, TestItem.AddressA, 3));
        fixture.Reopen();

        fixture.Replay(writer => SetSlots(writer, TestItem.AddressB, 1));
        fixture.Session.CleanStorage(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Count(Columns.Clears), Is.Zero);
            Assert.That(fixture.Count(Columns.Storage), Is.EqualTo(2), "slot 3 of A and slot 1 of B remain");
        }
    }

    [Test]
    public void CleanStorage_handles_several_blocks_and_repeated_clears_at_once()
    {
        using Fixture fixture = new();
        fixture.Replay(writer => SetSlots(writer, TestItem.AddressA, 1, 2));
        fixture.Session.CleanStorage(CancellationToken.None);

        // Two blocks commit before one cleanup, and one block clears the same account twice.
        fixture.Replay(writer => ClearAndSetSlots(writer, TestItem.AddressA, 3));
        fixture.Replay(writer =>
        {
            ClearAndSetSlots(writer, TestItem.AddressB, 1);
            ClearAndSetSlots(writer, TestItem.AddressB, 2);
        });
        fixture.Session.CleanStorage(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Count(Columns.Clears), Is.Zero);
            Assert.That(fixture.Count(Columns.Storage), Is.EqualTo(3), "slot 3 of A and slots 1 and 2 of B remain");
        }
    }

    [TestCase(sizeof(ulong) - 1, typeof(InvalidDataException), TestName = "CleanStorage_rejects_a_short_marker")]
    [TestCase(sizeof(ulong) + 1, typeof(ArgumentException), TestName = "CleanStorage_rejects_an_oversized_marker")]
    public void CleanStorage_rejects_a_malformed_marker(int length, Type exception)
    {
        using Fixture fixture = CleanedFixtureWithMarker(length);

        Assert.That(() => fixture.Session.CleanStorage(CancellationToken.None), Throws.InstanceOf(exception));
    }

    [Test]
    public void CleanStorage_reads_an_empty_marker_as_absent()
    {
        using Fixture fixture = CleanedFixtureWithMarker(0);

        Assert.That(() => fixture.Session.CleanStorage(CancellationToken.None), Throws.Nothing);
    }

    private static Fixture CleanedFixtureWithMarker(int length)
    {
        Fixture fixture = new();
        fixture.Replay(writer => SetSlots(writer, TestItem.AddressA, 1));
        fixture.Session.CleanStorage(CancellationToken.None);
        fixture.Replay(writer => ClearAndSetSlots(writer, TestItem.AddressA, 2));
        fixture.Scratch.GetColumnDb(Columns.Clears).PutSpan(TestItem.AddressA.ToAccountPath.Bytes, new byte[length]);
        return fixture;
    }

    [Test]
    public void CleanStorage_skips_an_account_whose_marker_is_gone()
    {
        using Fixture fixture = new();
        fixture.Replay(writer => SetSlots(writer, TestItem.AddressA, 1));
        fixture.Session.CleanStorage(CancellationToken.None);
        fixture.Replay(writer => ClearAndSetSlots(writer, TestItem.AddressA, 2));
        fixture.Scratch.GetColumnDb(Columns.Clears).Remove(TestItem.AddressA.ToAccountPath.Bytes);

        fixture.Session.CleanStorage(CancellationToken.None);

        Assert.That(fixture.Count(Columns.Storage), Is.EqualTo(2), "with no marker, the old slot 1 is not dropped");
    }

    [Test]
    public void CleanStorage_after_the_first_leaves_markers_no_block_of_the_session_wrote()
    {
        using Fixture fixture = new();
        fixture.Replay(writer => SetSlots(writer, TestItem.AddressA, 1));
        fixture.Session.CleanStorage(CancellationToken.None);
        fixture.Replay(writer => ClearAndSetSlots(writer, TestItem.AddressA, 2));
        // Only a full scan finds this marker, because no block of this session cleared the account.
        fixture.Scratch.GetColumnDb(Columns.Clears).PutSpan(TestItem.AddressB.ToAccountPath.Bytes, new byte[sizeof(ulong)]);

        fixture.Session.CleanStorage(CancellationToken.None);
        Assert.That(fixture.Count(Columns.Clears), Is.EqualTo(1), "the targeted cleanup reads only the accounts of this session");

        fixture.Reopen();
        fixture.Session.CleanStorage(CancellationToken.None);
        Assert.That(fixture.Count(Columns.Clears), Is.Zero, "the full scan of a reopened session drops the marker");
    }

    private static void SetSlots(IWorldStateScopeProvider.IWorldStateWriteBatch writer, Address address, params ulong[] slots)
    {
        writer.Set(address, Contract);
        using IWorldStateScopeProvider.IStorageWriteBatch storage = writer.CreateStorageWriteBatch(address, slots.Length);
        foreach (ulong slot in slots) storage.Set(new UInt256(slot), UInt256.One);
    }

    private static void ClearAndSetSlots(IWorldStateScopeProvider.IWorldStateWriteBatch writer, Address address, params ulong[] slots)
    {
        writer.Set(address, Contract);
        using IWorldStateScopeProvider.IStorageWriteBatch storage = writer.CreateStorageWriteBatch(address, slots.Length);
        storage.Clear();
        foreach (ulong slot in slots) storage.Set(new UInt256(slot), UInt256.One);
    }

    /// <summary>A ready session anchored at an empty genesis, over a scratch database that outlives the session.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly MemDb _code = new();
        private readonly ReopenableDbFactory _factory = new();
        private readonly BlockHeader _anchor = Build.A.BlockHeader.WithNumber(0).WithStateRoot(Keccak.EmptyTreeHash).TestObject;
        private BlockHeader _head;

        public Fixture()
        {
            _head = _anchor;
            Session = Open();
            Session.ImportGenesis([], CancellationToken.None);
        }

        public BulkFillSession Session { get; private set; }

        public IColumnsDb<Columns> Scratch => _factory.Db!;

        public void Replay(Action<IWorldStateScopeProvider.IWorldStateWriteBatch> write)
        {
            BlockHeader block = Build.A.BlockHeader.WithParent(_head).WithStateRoot(Keccak.EmptyTreeHash).TestObject;
            Session.BeginBlock(block);
            Session.StageFinalState(() => new Snapshot(write));
            Session.CommitBlock();
            _head = block;
        }

        public void Reopen()
        {
            Session.Dispose();
            Session = Open();
        }

        public int Count(Columns column)
        {
            int count = 0;
            foreach (byte[] _ in Scratch.GetColumnDb(column).GetAllKeys()) count++;
            return count;
        }

        public void Dispose()
        {
            Session.Dispose();
            _code.Dispose();
        }

        private BulkFillSession Open() => new(_factory, _code, TestItem.KeccakA, _anchor, false);
    }

    private sealed class Snapshot(Action<IWorldStateScopeProvider.IWorldStateWriteBatch> write) : IWorldStateScopeProvider.IBlockChangeSnapshot
    {
        public void WriteTo(IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch) => write(writeBatch);

        public void Dispose() { }
    }

    /// <summary>Hands every session the same in-memory scratch database, as a restart reopens the same directory.</summary>
    private sealed class ReopenableDbFactory : IDbFactory
    {
        public IColumnsDb<Columns>? Db { get; private set; }

        public IColumnsDb<T> CreateColumnsDb<T>(DbSettings dbSettings) where T : struct, Enum
        {
            if (Db is not IColumnsDb<T> db)
            {
                SnapshotableMemColumnsDb<T> created = new(dbSettings.DbName);
                db = created;
                if (created is IColumnsDb<Columns> scratch) Db = scratch;
            }
            return db;
        }

        public IDb CreateDb(DbSettings dbSettings) => new MemDb(dbSettings.DbName);
    }
}
