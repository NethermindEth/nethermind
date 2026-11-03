// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using NUnit.Framework;
using Paprika.Crypto;
using Paprika.Data;
using Paprika.Store;
using static Paprika.Tests.Values;

namespace Paprika.Tests.Store;

public class DbTests
{
    private const int MB = 1024 * 1024;
    private const int MB16 = 16 * MB;
    private const int MB64 = 64 * MB;
    private const int MB256 = 256 * MB;

    [Test]
    public async Task Simple()
    {
        const int max = 1024;

        using PagedDb db = PagedDb.NativeMemoryDb(MB256);

        for (int i = 0; i < max; i++)
        {
            Keccak key = GetKey(i);

            using IBatch batch = db.BeginNextBatch();

            byte[] value = GetValue(i);

            batch.SetAccount(key, value);
            batch.SetStorage(key, key, value);
            batch.SetRaw(Key.Merkle(NibblePath.FromKey(key)), value);

            await batch.Commit(CommitOptions.FlushDataAndRoot);
        }

        using IBatch read = db.BeginNextBatch();

        AssertAll(read);
        return;

        static void AssertAll(IReadOnlyBatch read)
        {
            for (int i = 0; i < max; i++)
            {
                Keccak key = GetKey(i);
                byte[] expected = GetValue(i);

                read.ShouldHaveAccount(key, expected);
                read.AssertStorageValue(key, key, expected);
                Assert.That(read.TryGet(Key.Merkle(NibblePath.FromKey(key)), out ReadOnlySpan<byte> value), Is.True);
                if (!value.SequenceEqual(expected))
                {
                    Assert.Fail(
                        $"Failed at {i} where {ParseValue(value)} should have been equal to {ParseValue(expected)}");
                }
            }
        }
    }

    [TestCase(100_000, 1, TestName = "Long history, single account")]
    [TestCase(500, 2_000, TestName = "Short history, many accounts")]
    public async Task Page_reuse(int blockCount, int accountsCount)
    {
        const int size = MB256;

        using PagedDb db = PagedDb.NativeMemoryDb(size);

        for (int i = 0; i < blockCount; i++)
        {
            using IBatch batch = db.BeginNextBatch();

            for (int account = 0; account < accountsCount; account++)
            {
                batch.SetAccount(GetKey(i), GetValue(i));
            }

            await batch.Commit(CommitOptions.FlushDataOnly);
        }

        Console.WriteLine($"Uses {db.Megabytes:P}MB out of pre-allocated {size / MB}MB od disk.");

        AssertPageMetadataAssigned(db);
    }

    [Test]
    public async Task Readonly_transaction_block_till_they_are_released()
    {
        const int size = MB16;
        const int blocksDuringReadAcquired = 500; // the number should be smaller than the number of buckets in the root
        const int blocksPostRead = 10_000;
        const int start = -1;

        using PagedDb db = PagedDb.NativeMemoryDb(size);

        // write first value
        using (IBatch block = db.BeginNextBatch())
        {
            block.SetAccount(Key0, GetValue(start));
            await block.Commit(CommitOptions.FlushDataOnly);
        }

        double actualSize = await SetAndSnapshotSize(db);

        // write again
        for (int i = 0; i < blocksPostRead; i++)
        {
            using IBatch block = db.BeginNextBatch();

            byte[] value = GetValue(i + start);

            block.SetAccount(Key0, value);
            await block.Commit(CommitOptions.FlushDataOnly);
        }

        Assert.That(db.Megabytes, Is.EqualTo(actualSize), "Database should not grow without read transaction active.");

        Console.WriteLine($"Uses {db.Megabytes:P}MB out of pre-allocated {actualSize / MB}MB od disk.");

        AssertPageMetadataAssigned(db);
        return;

        static async Task<double> SetAndSnapshotSize(PagedDb db)
        {
            using IReadOnlyBatch read = db.BeginReadOnlyBatch();

            for (int i = 0; i < blocksDuringReadAcquired; i++)
            {
                using IBatch block = db.BeginNextBatch();

                // assert previous
                block.ShouldHaveAccount(Key0, GetValue(i + start));

                // write current
                block.SetAccount(Key0, GetValue(i));

                block.VerifyDbPagesOnCommit();
                await block.Commit(CommitOptions.FlushDataOnly);

                read.ShouldHaveAccount(Key0, GetValue(start));
            }

            return db.Megabytes;
        }
    }

    [Test]
    public async Task State_and_storage()
    {
        const int size = MB64;
        using PagedDb db = PagedDb.NativeMemoryDb(size);

        const int count = 10_000;

        using (IBatch batch = db.BeginNextBatch())
        {
            for (int i = 0; i < count; i++)
            {
                Keccak address = GetStorageAddress(i);

                batch.SetAccount(Key0, GetValue(i));
                batch.SetStorage(Key0, address, GetValue(i));
            }

            await batch.Commit(CommitOptions.FlushDataOnly);
        }

        using (IReadOnlyBatch read = db.BeginReadOnlyBatch())
        {
            for (int i = 0; i < count; i++)
            {
                Keccak address = GetStorageAddress(i);
                read.AssertStorageValue(Key0, address, GetValue(i));
            }
        }

        AssertPageMetadataAssigned(db);
    }

    [Test]
    public async Task Cross_block_entries()
    {
        const int size = MB64;
        using PagedDb db = PagedDb.NativeMemoryDb(size);

        const int count = 1600;

        for (int i = 0; i < count; i++)
        {
            using IBatch batch = db.BeginNextBatch();

            Keccak address = GetStorageAddress(i);

            batch.SetAccount(Key0, GetValue(i));
            batch.SetStorage(Key0, address, GetValue(i));

            await batch.Commit(CommitOptions.FlushDataAndRoot);
        }

        using (IReadOnlyBatch read = db.BeginReadOnlyBatch())
        {
            for (int i = 0; i < count; i++)
            {
                Keccak address = GetStorageAddress(i);
                read.AssertStorageValue(Key0, address, GetValue(i));
            }
        }

        AssertPageMetadataAssigned(db);
    }

    [Test]
    public async Task Spin_large()
    {
        Keccak account = Keccak.EmptyTreeHash;

        const int size = MB256;
        using PagedDb db = PagedDb.NativeMemoryDb(size);

        const int batches = 25;
        const int storageSlots = 20_000;
        const int storageKeyLength = 32;

        byte[] value = new byte[32];
        byte[] storageKeys = new byte[storageSlots + storageKeyLength];

        Random random = new(13);
        random.NextBytes(storageKeys);
        random.NextBytes(value);

        using CompositeDisposable<IVisitableReadOnlyBatch> reads = new();

        for (int i = 0; i < batches; i++)
        {
            using IBatch batch = db.BeginNextBatch();

            for (int slot = 0; slot < storageSlots; slot++)
            {
                batch.SetStorage(account, GetRandomStorageAddress(slot), value);
            }

            await batch.Commit(CommitOptions.FlushDataAndRoot);

            reads.Add(db.BeginReadOnlyBatch());
        }

        foreach (IVisitableReadOnlyBatch read in reads)
        {
            for (int slot = 0; slot < storageSlots; slot++)
            {
                read.AssertStorageValue(account, GetRandomStorageAddress(slot), value);
            }
        }

        return;

        Keccak GetRandomStorageAddress(int i)
        {
            Keccak result = default;
            storageKeys
                .AsSpan(i, storageKeyLength)
                .CopyTo(result.BytesAsSpan);
            return result;
        }
    }

    private static void AssertPageMetadataAssigned(PagedDb db)
    {
        PageType[] types = Enum.GetValues<PageType>().Where(static type => type != PageType.None).ToArray();

        foreach (Page page in db.UnsafeEnumerateNonRoot())
        {
            PageHeader header = page.Header;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(header.BatchId, Is.GreaterThan(0U));
                Assert.That(header.PageType, Is.AnyOf(types));
                Assert.That(header.PaprikaVersion, Is.EqualTo(1));
            }
        }
    }

    private static Keccak GetStorageAddress(int i)
    {
        Keccak address = Key1A;
        BinaryPrimitives.WriteInt32LittleEndian(address.BytesAsSpan, i);
        return address;
    }

    private static Keccak GetKey(int i)
    {
        Keccak keccak = Keccak.EmptyTreeHash;
        BinaryPrimitives.WriteInt32LittleEndian(keccak.BytesAsSpan, i);
        return keccak;
    }

    private static byte[] GetValue(int i) => BitConverter.GetBytes(i);

    private static int ParseValue(ReadOnlySpan<byte> bytes) => BitConverter.ToInt32(bytes);
}
