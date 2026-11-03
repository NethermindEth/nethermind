// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using NUnit.Framework;
using Paprika.Crypto;
using Paprika.Data;
using Paprika.Store;

namespace Paprika.Tests.Store;

public class ClearTests
{
    private const long Mb = 1024 * 1024;
    private const int Seed = 13;

    private static readonly byte[] Value = Bytes.FromHexString("0x0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20");

    [Test]
    public async Task Clear_empties_the_store_and_keeps_old_readers_intact()
    {
        using PagedDb db = PagedDb.NativeMemoryDb(64 * Mb);
        Random random = new(Seed);
        Keccak[] accounts = random.NextKeccaks(500);
        Keccak[] slots = random.NextKeccaks(500);
        Keccak contract = random.NextKeccak();

        using (IBatch batch = db.BeginNextBatch())
        {
            batch.SetMetadata(1, Values.Key1);
            foreach (Keccak account in accounts)
            {
                batch.SetAccount(account, Value);
            }

            foreach (Keccak slot in slots)
            {
                batch.SetStorage(contract, slot, Value);
            }

            await batch.Commit(CommitOptions.FlushDataAndRoot);
        }

        uint accountCounter = TreeShapeVisitor.VisitRoot(db).AccountCounter;
        using IReadOnlyBatch beforeClear = db.BeginReadOnlyBatch();

        using (IBatch batch = db.BeginNextBatch())
        {
            batch.Clear();
            await batch.Commit(CommitOptions.FlushDataAndRoot);
        }

        using IReadOnlyBatch afterClear = db.BeginReadOnlyBatch();
        afterClear.VerifyNoPagesMissing();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(CountPresent(afterClear, accounts, slots, contract), Is.Zero);
            Assert.That(afterClear.Metadata.BlockNumber, Is.Zero);
            Assert.That(TreeShapeVisitor.VisitRoot(db).AccountCounter, Is.EqualTo(accountCounter), "Contract ids are never reused");
            Assert.That(CountPresent(beforeClear, accounts, slots, contract), Is.EqualTo(accounts.Length + slots.Length));
            Assert.That(beforeClear.Metadata.BlockNumber, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Clear_reuses_previous_pages_only_after_old_readers_release()
    {
        const int batchesPerPhase = 5;
        using PagedDb db = PagedDb.NativeMemoryDb(64 * Mb);
        Random random = new(Seed);

        Keccak[] cleared = random.NextKeccaks(10_000);
        await SetAccounts(db, cleared);
        IReadOnlyBatch beforeClear = db.BeginReadOnlyBatch();

        using (IBatch batch = db.BeginNextBatch())
        {
            batch.Clear();
            await batch.Commit(CommitOptions.FlushDataAndRoot);
        }

        // Every batch adds accounts, so only reused pages keep the file from growing.
        List<uint> whileReaderOpen = [];
        for (int i = 0; i < batchesPerPhase; i++)
        {
            whileReaderOpen.Add(await SetAccounts(db, random.NextKeccaks(10)));
        }

        int stillReadable = cleared.Count(account => beforeClear.TryGet(Key.Account(account), out _));
        beforeClear.Dispose();

        List<uint> afterRelease = [];
        for (int i = 0; i < batchesPerPhase; i++)
        {
            afterRelease.Add(await SetAccounts(db, random.NextKeccaks(200)));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stillReadable, Is.EqualTo(cleared.Length));
            Assert.That(whileReaderOpen, Is.Ordered.Ascending.And.Unique);
            Assert.That(afterRelease, Is.All.EqualTo(whileReaderOpen[^1]));
        }
    }

    [Test]
    public async Task Clear_mid_batch_keeps_only_later_writes()
    {
        using PagedDb db = PagedDb.NativeMemoryDb(16 * Mb);
        await db.SetAccountAndCommit(Value);

        using (IBatch batch = db.BeginNextBatch())
        {
            // Before the clear, the batch abandons the committed pages it copies, and the dropped contract frees pages it wrote
            // for immediate reuse. The clear leaves all of them to its reclaim range, so the batch must stop tracking them.
            batch.SetAccount(Values.Key2, Value);
            batch.SetStorage(Values.Key1, Values.Key3, Value);
            foreach (Keccak slot in new Random(Seed).NextKeccaks(500))
            {
                batch.SetStorage(Values.Key0, slot, Value);
            }

            batch.DeleteByPrefix(Key.StorageCell(NibblePath.FromKey(Values.Key0), NibblePath.Empty));
            batch.Clear();
            batch.SetAccount(Values.Key4, Value);
            batch.SetStorage(Values.Key1, Values.Key5, Value);

            AssertOnlyLaterWrites(batch);
            await batch.Commit(CommitOptions.FlushDataAndRoot);
        }

        using IReadOnlyBatch read = db.BeginReadOnlyBatch();
        read.VerifyNoPagesMissing();
        AssertOnlyLaterWrites(read);
        Assert.That(TreeShapeVisitor.VisitRoot(db).PagesClaimedTwice, Is.Zero);

        static void AssertOnlyLaterWrites(IReadOnlyBatch read)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(read.TryGet(Key.Account(Values.Key0), out _), Is.False, "committed before the clear");
                Assert.That(read.TryGet(Key.Account(Values.Key2), out _), Is.False, "written before the clear");
                Assert.That(read.TryGet(Key.StorageCell(NibblePath.FromKey(Values.Key1), Values.Key3), out _), Is.False, "written before the clear");
                Assert.That(read.TryGet(Key.Account(Values.Key4), out _), Is.True);
                Assert.That(read.TryGet(Key.StorageCell(NibblePath.FromKey(Values.Key1), Values.Key5), out _), Is.True);
            }
        }
    }

    private static int CountPresent(IReadOnlyBatch read, Keccak[] accounts, Keccak[] slots, Keccak contract) =>
        accounts.Count(account => read.TryGet(Key.Account(account), out _)) +
        slots.Count(slot => read.TryGet(Key.StorageCell(NibblePath.FromKey(contract), slot), out _));

    /// <summary>Sets the accounts in a batch of its own, verifies no page leaked and returns the next free page.</summary>
    private static async Task<uint> SetAccounts(PagedDb db, Keccak[] accounts)
    {
        using (IBatch batch = db.BeginNextBatch())
        {
            foreach (Keccak account in accounts)
            {
                batch.SetAccount(account, Value);
            }

            await batch.Commit(CommitOptions.FlushDataAndRoot);
        }

        using IReadOnlyBatch read = db.BeginReadOnlyBatch();
        read.VerifyNoPagesMissing();
        return db.NextFreePage;
    }
}
