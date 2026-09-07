// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

[TestFixture]
public class OrphanStorageRowSweepTests
{
    private static readonly Address Living = TestItem.AddressA;
    private static readonly Address DestroyedAtBirth = TestItem.AddressB;
    private static readonly Address RevivedEmpty = TestItem.AddressC;
    private static readonly Address LateBloomer = TestItem.AddressD;

    private SnapshotableMemColumnsDb<FlatHistoryColumns> _history = null!;
    private SnapshotableMemColumnsDb<FlatDbColumns> _flat = null!;
    private HistoryRowFormat _rowFormat = null!;
    private HistoryAvailability _availability = null!;

    [SetUp]
    public void SetUp()
    {
        _history = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _flat = new SnapshotableMemColumnsDb<FlatDbColumns>();
        (_availability, _rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_history, new FlatDbConfig { HistoryEnabled = true });

        HistoryColumnsWriter.RecordAccount(_history, Living, block: 5, new Account(1, 1, TestItem.KeccakA, Keccak.OfAnEmptyString));
        HistoryColumnsWriter.RecordStorage(_history, Living, 1, block: 5, [0x0A]);
        HistoryColumnsWriter.RecordStorage(_history, Living, 1, block: 8, [0x0B]);

        HistoryColumnsWriter.RecordAccount(_history, DestroyedAtBirth, block: 7, null);
        HistoryColumnsWriter.RecordStorage(_history, DestroyedAtBirth, 1, block: 7, [0x0C]);

        HistoryColumnsWriter.RecordAccount(_history, RevivedEmpty, block: 7, new Account(0, 0));
        HistoryColumnsWriter.RecordStorage(_history, RevivedEmpty, 1, block: 7, [0x0D]);
        HistoryColumnsWriter.RecordStorage(_history, RevivedEmpty, 2, block: 7, [0x0E]);

        HistoryColumnsWriter.RecordAccount(_history, LateBloomer, block: 3, new Account(0, 0));
        HistoryColumnsWriter.RecordAccount(_history, LateBloomer, block: 9, new Account(1, 1, TestItem.KeccakB, Keccak.OfAnEmptyString));
        HistoryColumnsWriter.RecordStorage(_history, LateBloomer, 1, block: 9, [0x0F]);
    }

    [TearDown]
    public void TearDown()
    {
        _history.Dispose();
        _flat.Dispose();
    }

    [Test]
    public void Rows_whose_account_had_no_storage_at_their_block_are_deleted_and_the_rest_stay()
    {
        using OrphanStorageRowSweep sweep = new(_history, _flat, Substitute.For<IPersistenceManager>(), _rowFormat, LimboLogs.Instance);

        OrphanStorageRowReport? announced = null;
        sweep.Completed += completed => announced = completed;
        OrphanStorageRowReport report = sweep.RunToCompletion(repair: true, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(report, Is.EqualTo(new OrphanStorageRowReport(RowsScanned: 6, OrphanRows: 3, OrphanAccounts: 2)));
            Assert.That(announced, Is.EqualTo(report), "whoever derives data from the rows learns that they changed");
            Assert.That(sweep.AlreadyHandled, Is.True);
            Assert.That(Slot(Living, 1, 5), Is.EqualTo(0x0A), "a contract whose account row carries a storage root keeps its rows");
            Assert.That(Slot(Living, 1, 8), Is.EqualTo(0x0B));
            Assert.That(Slot(DestroyedAtBirth, 1, 7), Is.Zero, "a row at a block where the account row is a deletion cannot be part of any state; this is a contract created and destroyed in one block");
            Assert.That(Slot(RevivedEmpty, 1, 7), Is.Zero, "a row at a block where the account row says empty storage root contradicts that root; this is the same contract revived by a transfer in that block");
            Assert.That(Slot(RevivedEmpty, 2, 7), Is.Zero);
            Assert.That(Slot(LateBloomer, 1, 9), Is.EqualTo(0x0F), "an account that had no storage earlier and gained it later keeps the rows written once it had a storage root");
        }
    }

    [Test]
    public void A_check_counts_without_deleting_or_stamping()
    {
        using OrphanStorageRowSweep sweep = new(_history, _flat, Substitute.For<IPersistenceManager>(), _rowFormat, LimboLogs.Instance);

        OrphanStorageRowReport report = sweep.RunToCompletion(repair: false, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(report, Is.EqualTo(new OrphanStorageRowReport(RowsScanned: 6, OrphanRows: 3, OrphanAccounts: 2)));
            Assert.That(Slot(DestroyedAtBirth, 1, 7), Is.EqualTo(0x0C));
            Assert.That(sweep.AlreadyHandled, Is.False);
        }
    }

    [Test]
    public void A_pass_that_runs_out_of_budget_yields_at_a_prefix_boundary_and_resumes_from_its_cursor()
    {
        using OrphanStorageRowSweep sweep = new(_history, _flat, Substitute.For<IPersistenceManager>(), _rowFormat, LimboLogs.Instance);

        bool completed = sweep.RunOnePass(repair: true, maxRows: 1, TimeSpan.MaxValue, CancellationToken.None);
        bool handledAfterOnePass = sweep.AlreadyHandled;
        OrphanStorageRowReport report = sweep.RunToCompletion(repair: true, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(completed, Is.False);
            Assert.That(handledAfterOnePass, Is.False, "nothing is stamped until every row has been seen");
            Assert.That(report, Is.EqualTo(new OrphanStorageRowReport(RowsScanned: 6, OrphanRows: 3, OrphanAccounts: 2)), "the later passes continue from the cursor, so every row is counted exactly once");
            Assert.That(Slot(DestroyedAtBirth, 1, 7), Is.Zero);
            Assert.That(Slot(RevivedEmpty, 2, 7), Is.Zero);
            Assert.That(sweep.AlreadyHandled, Is.True);
        }
    }

    private int Slot(Address address, UInt256 slot, ulong block)
    {
        HistoryReader reader = new(_flat, _history, _availability, _rowFormat, LimboLogs.Instance);
        if (!reader.TryGetStorage(block, address, slot, out SlotValue value)) return 0;

        ReadOnlySpan<byte> bytes = value.AsReadOnlySpan;
        return bytes.IsZero() ? 0 : bytes[^1];
    }
}
