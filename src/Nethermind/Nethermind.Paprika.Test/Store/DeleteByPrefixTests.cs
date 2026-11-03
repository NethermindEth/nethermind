// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using NUnit.Framework;
using Paprika.Crypto;
using Paprika.Data;
using Paprika.Store;
using static Paprika.Tests.Store.TreeShapeVisitor;

namespace Paprika.Tests.Store;

public class DeleteByPrefixTests
{
    private const long Mb = 1024 * 1024;
    private const int Seed = 13;
    private const int NeighbourSlots = 100;

    /// <summary>
    /// Contract ids start at 1 and ids 0 to 15 share one storage subtree, so 15 contracts fill it. The deleted one sits in the
    /// middle, with neighbours on both sides.
    /// </summary>
    private const int GroupSize = 15;
    private const int Target = 6;

    /// <summary>With 32-byte values, 3,000 slots turn a contract's subtree into a data page and 25,000 make it fan out.</summary>
    private static readonly byte[] Value = Bytes.FromHexString("0x000000000000000000000000000000000000000000000000000000000000002a");

    private static readonly byte[] NewValue = Bytes.FromHexString("0x000000000000000000000000000000000000000000000000000000000000002b");

    [Test]
    public async Task Deleting_contract_storage_removes_every_slot([Values(50, 3_000, 25_000)] int slots)
    {
        Random random = new(Seed);
        Keccak[] contracts = random.NextKeccaks(GroupSize);
        Keccak[][] slotsOf = [.. contracts.Select((_, i) => random.NextKeccaks(i == Target ? slots : NeighbourSlots))];

        using PagedDb db = PagedDb.NativeMemoryDb(256 * Mb);
        await db.Commit(batch => SetSlots(batch, contracts, slotsOf));
        await db.Commit(batch => DeleteContract(batch, contracts[Target]));

        using IReadOnlyBatch read = db.BeginReadOnlyBatch();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(CountMismatches(read, contracts[Target], slotsOf[Target], null), Is.Zero, "Slots of the deleted contract are still readable");
            Assert.That(CountNeighbourMismatches(read, contracts, slotsOf), Is.Zero, "Neighbour slots changed");
            Assert.That(read.VerifyNoPagesMissing, Throws.Nothing);
        }
    }

    [Test]
    public async Task One_nibble_prefix_on_fan_out_page_deletes_only_its_buckets([Values(0x0, 0x5, 0xF)] byte nibble)
    {
        Random random = new(Seed);
        Keccak contract = random.NextKeccak();
        Keccak[] slots = random.NextKeccaks(25_000);

        using PagedDb db = PagedDb.NativeMemoryDb(256 * Mb);
        await db.Commit(batch => SetSlots(batch, contract, slots, Value));
        Assert.That(VisitRoot(db).FanOutChildren, Is.GreaterThan(0), "The contract's storage should have a fan-out data page");

        await db.Commit(batch => batch.DeleteByPrefix(Key.StorageCell(NibblePath.FromKey(contract), NibblePath.Single(nibble, 0))));

        using IReadOnlyBatch read = db.BeginReadOnlyBatch();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(CountMismatches(read, contract, slots.Where(slot => FirstNibble(slot) == nibble), null), Is.Zero, "Slots under the prefix are still readable");
            Assert.That(CountMismatches(read, contract, slots.Where(slot => FirstNibble(slot) != nibble), Value), Is.Zero, "Slots outside the prefix changed");
            Assert.That(read.VerifyNoPagesMissing, Throws.Nothing);
        }
    }

    [Test]
    public async Task Deleting_local_key_also_deletes_its_side_page_copy([Values] bool wholeContract)
    {
        const byte deleted = 0x5;
        Random random = new(Seed);
        Keccak contract = random.NextKeccak();
        Keccak[] slots = random.NextKeccaks(3_000);

        // 3,000 slots make the contract's subtree a data page. Its one-nibble keys are local keys, and sixteen 250-byte values
        // overflow its map: those written before the overflow from nibble 3 on, including 5, move to the side page.
        byte[] large = new byte[250];
        large.AsSpan().Fill(0x2a);

        using PagedDb db = PagedDb.NativeMemoryDb(64 * Mb);
        await db.Commit(batch => SetSlots(batch, contract, slots, Value));
        await db.Commit(batch =>
        {
            for (byte nibble = 0; nibble < 16; nibble++)
            {
                batch.SetRaw(Key.StorageCell(NibblePath.FromKey(contract), NibblePath.Single(nibble, 0)), large);
            }
        });

        // A whole contract is a bucket of the subtree it shares with other contracts: dropping it abandons the data page under
        // the bucket, side page included.
        await db.Commit(batch => batch.DeleteByPrefix(Key.StorageCell(NibblePath.FromKey(contract), wholeContract ? NibblePath.Empty : NibblePath.Single(deleted, 0))));

        using IReadOnlyBatch read = db.BeginReadOnlyBatch();
        using (Assert.EnterMultipleScope())
        {
            for (byte nibble = 0; nibble < 16; nibble++)
            {
                bool found = read.TryGet(Key.StorageCell(NibblePath.FromKey(contract), NibblePath.Single(nibble, 0)), out ReadOnlySpan<byte> value);
                Assert.That(found ? value.ToArray() : null, Is.EqualTo(wholeContract || nibble == deleted ? null : large), $"One-nibble key {nibble:x}");
            }

            Assert.That(CountMismatches(read, contract, slots.Where(slot => wholeContract || FirstNibble(slot) == deleted), null), Is.Zero, "Slots under the prefix are still readable");
            Assert.That(CountMismatches(read, contract, slots.Where(slot => !wholeContract && FirstNibble(slot) != deleted), Value), Is.Zero, "Slots outside the prefix changed");
            Assert.That(read.VerifyNoPagesMissing, Throws.Nothing);
        }
    }

    [Test]
    public async Task Dropped_bucket_does_not_count_towards_a_fan_out()
    {
        // The contract's subtree root turns into a fan-out page once all 16 of its buckets are marked as data pages. Bucket 3 is
        // dropped while 15 are, so when bucket F becomes the 16th data page, the page must stay as it is: a fan-out would read
        // the dropped bucket as a data page.
        const int dropped = 0x3;
        const int last = 0xF;
        const int parts = 4;
        const int slotsPerPart = 300;
        Random random = new(Seed);
        Keccak contract = random.NextKeccak();
        Keccak[][] slotsUnder = [.. Enumerable.Range(0, 16).Select(nibble => SlotsUnder(random, nibble, parts * slotsPerPart))];
        int[] allButLast = [.. Enumerable.Range(0, 16).Where(nibble => nibble != last)];

        using PagedDb db = PagedDb.NativeMemoryDb(256 * Mb);
        await SetInParts(allButLast);
        await db.Commit(batch => batch.DeleteByPrefix(Key.StorageCell(NibblePath.FromKey(contract), NibblePath.Single(dropped, 0))));
        await SetInParts([last]);

        using IReadOnlyBatch read = db.BeginReadOnlyBatch();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(CountMismatches(read, contract, slotsUnder[dropped], null), Is.Zero, "Slots under the prefix are still readable");
            Assert.That(CountMismatches(read, contract, slotsUnder.Where((_, nibble) => nibble != dropped).SelectMany(slots => slots), Value), Is.Zero, "Slots outside the prefix changed");
            Assert.That(read.VerifyNoPagesMissing, Throws.Nothing);
        }

        // A bucket is marked as a data page when a later write passes through it, hence the final write to each nibble.
        async Task SetInParts(int[] nibbles)
        {
            for (int part = 0; part < parts; part++)
            {
                await db.Commit(batch =>
                {
                    foreach (int nibble in nibbles)
                    {
                        SetSlots(batch, contract, slotsUnder[nibble][(part * slotsPerPart)..((part + 1) * slotsPerPart)], Value);
                    }
                });
            }

            await db.Commit(batch =>
            {
                foreach (int nibble in nibbles)
                {
                    batch.SetStorage(contract, slotsUnder[nibble][0], Value);
                }
            });
        }
    }

    [Test]
    public async Task Dropped_storage_pages_are_reused()
    {
        Random random = new(Seed);
        Keccak contract = random.NextKeccak();
        Keccak[] slots = random.NextKeccaks(3_000);

        using PagedDb db = PagedDb.NativeMemoryDb(64 * Mb);
        uint start = db.NextFreePage;
        await db.Commit(batch => SetSlots(batch, contract, slots, Value));
        uint insertGrowth = db.NextFreePage - start;

        await db.Commit(batch => DeleteContract(batch, contract));

        // Pages abandoned by a batch are reused once the reuse floor, two batches behind the writing one, passes it.
        await db.Commit(_ => { });
        await db.Commit(_ => { });

        uint beforeReinsert = db.NextFreePage;
        await db.Commit(batch => SetSlots(batch, contract, slots, Value));

        using IReadOnlyBatch read = db.BeginReadOnlyBatch();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.NextFreePage - beforeReinsert, Is.LessThan(insertGrowth / 10), $"The first insert took {insertGrowth} pages");
            Assert.That(CountMismatches(read, contract, slots, Value), Is.Zero, "Re-inserted slots are wrong");
        }
    }

    [Test]
    public async Task Deleting_and_re_putting_a_contract_in_one_batch_keeps_only_the_new_slots([Values] bool writtenThisBatch)
    {
        // Slots written earlier in the same batch live on pages the drop hands out again within the batch.
        Random random = new(Seed);
        Keccak[] contracts = random.NextKeccaks(GroupSize);
        Keccak[][] slotsOf = [.. contracts.Select((_, i) => random.NextKeccaks(i == Target ? 3_000 : NeighbourSlots))];
        Keccak[] rePut = [.. slotsOf[Target].Where((_, i) => i % 2 == 0)];
        Keccak[] dropped = [.. slotsOf[Target].Where((_, i) => i % 2 == 1)];

        using PagedDb db = PagedDb.NativeMemoryDb(64 * Mb);

        if (!writtenThisBatch)
        {
            await db.Commit(batch => SetSlots(batch, contracts, slotsOf));
        }

        await db.Commit(batch =>
        {
            if (writtenThisBatch)
            {
                SetSlots(batch, contracts, slotsOf);
            }

            DeleteContract(batch, contracts[Target]);
            SetSlots(batch, contracts[Target], rePut, NewValue);
        });

        using IReadOnlyBatch read = db.BeginReadOnlyBatch();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(CountMismatches(read, contracts[Target], rePut, NewValue), Is.Zero, "Re-put slots are wrong");
            Assert.That(CountMismatches(read, contracts[Target], dropped, null), Is.Zero, "Dropped slots are still readable");
            Assert.That(CountNeighbourMismatches(read, contracts, slotsOf), Is.Zero, "Neighbour slots changed");
            Assert.That(read.VerifyNoPagesMissing, Throws.Nothing);
        }
    }

    [Test]
    public async Task Disposing_a_batch_after_a_drop_keeps_the_committed_tree()
    {
        Random random = new(Seed);
        Keccak[] contracts = random.NextKeccaks(GroupSize);
        Keccak[][] slotsOf = [.. contracts.Select((_, i) => random.NextKeccaks(i == Target ? 3_000 : NeighbourSlots))];
        Keccak slot = random.NextKeccak();

        using PagedDb db = PagedDb.NativeMemoryDb(64 * Mb);
        await db.Commit(batch => SetSlots(batch, contracts, slotsOf));

        using (IBatch batch = db.BeginNextBatch())
        {
            DeleteContract(batch, contracts[Target]);
            SetSlots(batch, contracts[Target], slotsOf[Target][..100], NewValue);
        }

        using (IReadOnlyBatch read = db.BeginReadOnlyBatch())
        using (Assert.EnterMultipleScope())
        {
            Assert.That(CountMismatches(read, contracts[Target], slotsOf[Target], Value), Is.Zero, "The uncommitted drop changed the contract");
            Assert.That(read.VerifyNoPagesMissing, Throws.Nothing);
        }

        await db.Commit(batch => batch.SetStorage(contracts[Target], slot, NewValue));

        using (IReadOnlyBatch read = db.BeginReadOnlyBatch())
        using (Assert.EnterMultipleScope())
        {
            Assert.That(CountMismatches(read, contracts[Target], slotsOf[Target], Value), Is.Zero, "The next batch changed the contract");
            Assert.That(CountMismatches(read, contracts[Target], [slot], NewValue), Is.Zero, "The next batch's slot is wrong");
            Assert.That(read.VerifyNoPagesMissing, Throws.Nothing);
        }
    }

    [Test]
    public async Task Bottom_ids_and_side_pages_are_tracked_and_side_pages_dropped()
    {
        // Contracts sharing their first four nibbles share one ids page. It is created as a bottom page, and 150 ids overflow it into a child.
        Keccak[] contracts = new Random(Seed).NextKeccaks(150);
        for (int i = 0; i < contracts.Length; i++)
        {
            Span<byte> bytes = contracts[i].BytesAsSpan;
            bytes[0] = 0xAB;
            bytes[1] = 0xCD;
        }

        using PagedDb db = PagedDb.NativeMemoryDb(16 * Mb);
        await db.Commit(batch =>
        {
            foreach (Keccak contract in contracts)
            {
                batch.SetStorage(contract, contract, Value);
            }

            batch.SetThreeNibbleAccountsOverflowingIntoSidePage();
        });

        using (IReadOnlyBatch read = db.BeginReadOnlyBatch())
        using (Assert.EnterMultipleScope())
        {
            Assert.That(VisitRoot(db).BottomPageChildren, Is.GreaterThan(0), "The ids page should be visited as a bottom page with a child");
            Assert.That(read.VerifyNoPagesMissing, Throws.Nothing);
        }

        // The two nibbles of the prefix lead to the data page, so the prefix covers all of it, side page included.
        await db.Commit(batch => batch.DeleteByPrefix(Key.Account(NibblePath.FromKey(Bytes.FromHexString("0xab")))));

        using (IReadOnlyBatch read = db.BeginReadOnlyBatch())
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Enumerable.Range(0, 16).Count(nibble => read.TryGet(TestExtensions.ThreeNibbleAccount((byte)nibble), out _)), Is.Zero, "Accounts under the prefix are still readable");
            Assert.That(read.VerifyNoPagesMissing, Throws.Nothing);
        }
    }

    [Test]
    [Ignore("Probable upstream bug: StateRootPage.DeleteByPrefix with a prefix shorter than two nibbles clears only its buckets, not the keys shorter than two nibbles in its own map")]
    public async Task State_prefix_shorter_than_two_nibbles_deletes_keys_in_the_state_root_page([Values(0, 1)] int prefixNibbles)
    {
        byte[] account = Bytes.FromHexString("0xab");

        using PagedDb db = PagedDb.NativeMemoryDb(Mb);
        await db.Commit(batch => batch.SetRaw(Key.Account(NibblePath.FromKey(account).SliceTo(1)), Value));
        await db.Commit(batch => batch.DeleteByPrefix(Key.Account(NibblePath.FromKey(account).SliceTo(prefixNibbles))));

        using IReadOnlyBatch read = db.BeginReadOnlyBatch();
        Assert.That(read.TryGet(Key.Account(NibblePath.FromKey(account).SliceTo(1)), out _), Is.False);
    }

    private static int FirstNibble(in Keccak slot) => slot.BytesAsSpan[0] >> NibblePath.NibbleShift;

    private static Keccak[] SlotsUnder(Random random, int nibble, int count)
    {
        Keccak[] slots = random.NextKeccaks(count);
        for (int i = 0; i < count; i++)
        {
            Span<byte> bytes = slots[i].BytesAsSpan;
            bytes[0] = (byte)((nibble << NibblePath.NibbleShift) | (bytes[0] & NibblePath.NibbleMask));
        }

        return slots;
    }

    /// <summary>Writes the slots of each contract in turn, so that the contracts get consecutive ids.</summary>
    private static void SetSlots(IBatch batch, Keccak[] contracts, Keccak[][] slotsOf)
    {
        for (int i = 0; i < contracts.Length; i++)
        {
            SetSlots(batch, contracts[i], slotsOf[i], Value);
        }
    }

    private static void SetSlots(IBatch batch, in Keccak contract, IEnumerable<Keccak> slots, byte[] value)
    {
        foreach (Keccak slot in slots)
        {
            batch.SetStorage(contract, slot, value);
        }
    }

    private static void DeleteContract(IBatch batch, in Keccak contract) =>
        batch.DeleteByPrefix(Key.StorageCell(NibblePath.FromKey(contract), NibblePath.Empty));

    private static int CountNeighbourMismatches(IReadOnlyBatch read, Keccak[] contracts, Keccak[][] slotsOf) =>
        Enumerable.Range(0, contracts.Length).Where(i => i != Target).Sum(i => CountMismatches(read, contracts[i], slotsOf[i], Value));

    /// <summary>Counts the slots that do not read as <paramref name="expected"/>, where null means absent.</summary>
    private static int CountMismatches(IReadOnlyBatch read, in Keccak contract, IEnumerable<Keccak> slots, byte[]? expected)
    {
        int mismatches = 0;

        foreach (Keccak slot in slots)
        {
            bool found = read.TryGet(Key.StorageCell(NibblePath.FromKey(contract), slot), out ReadOnlySpan<byte> value);
            if (expected is null ? found : !found || !value.SequenceEqual(expected))
            {
                mismatches++;
            }
        }

        return mismatches;
    }
}
