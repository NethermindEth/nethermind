// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using NUnit.Framework;
using Paprika.Crypto;
using Paprika.Data;
using Paprika.Store;
using static Paprika.Tests.Store.TreeShapeVisitor;

namespace Paprika.Tests.Store;

public class DeleteTests
{
    private const long Mb = 1024 * 1024;
    private const int Seed = 17;

    private static readonly Keccak Contract = Values.Key2;

    public enum KeyKind
    {
        Account,
        Storage
    }

    public enum AbsentKind
    {
        Account,
        SlotOfUnknownContract,
        SlotOfKnownContract,
        Tombstone
    }

    [Test]
    public async Task Deleting_key_pushed_into_bottom_child_removes_it([Values] KeyKind kind, [Values] bool childWrittenThisBatch)
    {
        // 4-byte values: about 100 keys fill a page, so 150 keys overflow the bottom page into a child once, without turning it into a data page.
        Keccak[] keys = KeysSharingOneBottomPage(150);

        using PagedDb db = PagedDb.NativeMemoryDb(16 * Mb);

        if (!childWrittenThisBatch)
        {
            using IBatch fill = db.BeginNextBatch();
            SetAll(fill, kind, keys);
            await fill.Commit(CommitOptions.FlushDataAndRoot);
        }

        using (IBatch batch = db.BeginNextBatch())
        {
            if (childWrittenThisBatch)
            {
                SetAll(batch, kind, keys);
            }

            for (int i = 0; i < keys.Length; i += 2)
            {
                Set(batch, kind, keys[i], ReadOnlySpan<byte>.Empty);
            }

            AssertEveryOtherKeyDeleted(batch, kind, keys);
            await batch.Commit(CommitOptions.FlushDataAndRoot);
        }

        Assert.That(VisitRoot(db).BottomPageChildren, Is.GreaterThan(0), "The keys should have been pushed into bottom page children");

        using IReadOnlyBatch read = db.BeginReadOnlyBatch();
        AssertEveryOtherKeyDeleted(read, kind, keys);
        read.VerifyNoPagesMissing();
    }

    [Test]
    public async Task Deleting_an_absent_key_allocates_no_page([Values] AbsentKind kind)
    {
        using PagedDb db = PagedDb.NativeMemoryDb(Mb);
        byte[] value = Bytes.FromHexString("0x2a");

        using (IBatch batch = db.BeginNextBatch())
        {
            batch.SetAccount(Values.Key0, value);
            batch.SetStorage(Contract, Values.Key0, value);

            // Deleting a local key that stays in the map of a data page with a side page leaves a tombstone, which shadows the side page.
            batch.SetThreeNibbleAccountsOverflowingIntoSidePage();
            batch.SetRaw(TestExtensions.ThreeNibbleAccount(0), ReadOnlySpan<byte>.Empty);
            await batch.Commit(CommitOptions.FlushDataAndRoot);
        }

        uint nextFreePage = db.NextFreePage;
        uint accountCounter = VisitRoot(db).AccountCounter;

        using (IBatch batch = db.BeginNextBatch())
        {
            switch (kind)
            {
                case AbsentKind.Account:
                    batch.SetAccount(Values.Key1, ReadOnlySpan<byte>.Empty);
                    break;
                case AbsentKind.SlotOfUnknownContract:
                    batch.SetStorage(Values.Key1, Values.Key0, ReadOnlySpan<byte>.Empty);
                    break;
                case AbsentKind.SlotOfKnownContract:
                    batch.SetStorage(Contract, Values.Key1, ReadOnlySpan<byte>.Empty);
                    break;
                case AbsentKind.Tombstone:
                    batch.SetRaw(TestExtensions.ThreeNibbleAccount(0), ReadOnlySpan<byte>.Empty);
                    break;
            }

            await batch.Commit(CommitOptions.FlushDataAndRoot);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.NextFreePage, Is.EqualTo(nextFreePage), "No page should be allocated");
            Assert.That(VisitRoot(db).AccountCounter, Is.EqualTo(accountCounter), "No contract id should be allocated");
        }
    }

    [Test]
    public async Task Deleting_slots_frees_their_space_for_new_slots()
    {
        // With 32-byte values some of the 1,000 slots live in child bottom pages hanging directly under data pages, the leaves where
        // a delete used to keep a tombstone. With 3,000 slots those leaves have all turned into bottom pages.
        const int slots = 1_000;
        const int cycles = 4;
        byte[] value = Bytes.FromHexString("0x000000000000000000000000000000000000000000000000000000000000002a");
        Random random = new(Seed);
        Keccak[] current = random.NextKeccaks(slots);

        using PagedDb db = PagedDb.NativeMemoryDb(16 * Mb);
        await SetSlotsInOneBatch(db, current, value);
        uint afterFirstInsert = db.NextFreePage;
        Assert.That(VisitRoot(db).DataPageLeafEntries, Is.GreaterThan(0), "Some slots should live in leaves of data pages");

        for (int cycle = 0; cycle < cycles; cycle++)
        {
            await SetSlotsInOneBatch(db, current, []);

            using (IReadOnlyBatch read = db.BeginReadOnlyBatch())
            using (Assert.EnterMultipleScope())
            {
                Assert.That(VisitRoot(db).DataPageLeafEntries, Is.Zero, $"Deleting in cycle {cycle} left tombstones");
                Assert.That(current.Count(slot => TryGet(read, KeyKind.Storage, slot, out _)), Is.Zero, $"Slots deleted in cycle {cycle} are still readable");
            }

            current = random.NextKeccaks(slots);
            foreach (Keccak[] part in current.Chunk(400))
            {
                await SetSlotsInOneBatch(db, part, value);
            }
        }

        // Each cycle copies the live tree in its delete batch and again in its insert batches. Copies are reused once the history-depth
        // reuse floor passes, so the file settles near four times the first insert, while without reuse every cycle would add about twice that.
        Assert.That(db.NextFreePage, Is.LessThanOrEqualTo(5 * afterFirstInsert));
    }

    /// <summary>
    /// Random keys sharing the first three nibbles, so that as accounts they all land in one leaf page under the state root
    /// page and its data page. As slots of one contract they share a leaf page anyway.
    /// </summary>
    private static Keccak[] KeysSharingOneBottomPage(int count)
    {
        Keccak[] keys = new Random(Seed).NextKeccaks(count);

        for (int i = 0; i < count; i++)
        {
            Span<byte> bytes = keys[i].BytesAsSpan;
            bytes[0] = 0xAB;
            bytes[1] = (byte)(0xC0 | (bytes[1] & 0x0F));
        }

        return keys;
    }

    private static async Task SetSlotsInOneBatch(PagedDb db, Keccak[] slots, byte[] value)
    {
        using IBatch batch = db.BeginNextBatch();

        foreach (Keccak slot in slots)
        {
            Set(batch, KeyKind.Storage, slot, value);
        }

        await batch.Commit(CommitOptions.FlushDataAndRoot);
    }

    private static void SetAll(IBatch batch, KeyKind kind, Keccak[] keys)
    {
        for (int i = 0; i < keys.Length; i++)
        {
            Set(batch, kind, keys[i], i.ToByteArray());
        }
    }

    private static void Set(IBatch batch, KeyKind kind, in Keccak key, ReadOnlySpan<byte> value)
    {
        if (kind == KeyKind.Account)
        {
            batch.SetAccount(key, value);
        }
        else
        {
            batch.SetStorage(Contract, key, value);
        }
    }

    private static bool TryGet(IReadOnlyBatch read, KeyKind kind, in Keccak key, out ReadOnlySpan<byte> value) =>
        kind == KeyKind.Account
            ? read.TryGet(Key.Account(key), out value)
            : read.TryGet(Key.StorageCell(NibblePath.FromKey(Contract), key), out value);

    private static void AssertEveryOtherKeyDeleted(IReadOnlyBatch read, KeyKind kind, Keccak[] keys)
    {
        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < keys.Length; i++)
            {
                bool found = TryGet(read, kind, keys[i], out ReadOnlySpan<byte> value);

                if (i % 2 == 0)
                {
                    Assert.That(found, Is.False, $"Deleted key {i} is still readable");
                }
                else
                {
                    Assert.That(found, Is.True, $"Neighbour {i} is gone");
                    Assert.That(value.ToArray(), Is.EqualTo(i.ToByteArray()), $"Neighbour {i} has a wrong value");
                }
            }
        }
    }
}
