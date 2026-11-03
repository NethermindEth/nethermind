// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using NUnit.Framework;
using Paprika.Crypto;
using Paprika.Data;
using Paprika.Store;

namespace Paprika.Tests.Store;

public class PagedDbTests
{
    private const long Mb = 1024 * 1024;
    private const int Seed = 17;

    [Test]
    public async Task BiggerTest()
    {
        const int accounts = 1;
        const int merkleCount = 31;

        ushort counter = 0;

        using PagedDb db = PagedDb.NativeMemoryDb(256 * Mb, 2);

        Random random = new(Seed);

        using IBatch batch = db.BeginNextBatch();

        for (int i = 0; i < accounts; i++)
        {
            Keccak keccak = random.NextKeccak();

            // account first & data
            batch.SetRaw(Key.Account(keccak), GetData());
            batch.SetRaw(Key.StorageCell(NibblePath.FromKey(keccak), keccak), GetData());

            for (int j = 0; j < merkleCount; j++)
            {
                // all the Merkle values
                batch.SetRaw(Key.Merkle(NibblePath.FromKey(keccak).SliceTo(j)), GetData());
            }
        }

        await batch.Commit(CommitOptions.FlushDataAndRoot);

        // reset
        random = new(Seed);
        counter = 0;

        AssertData();
        return;

        void AssertData()
        {
            using IReadOnlyBatch read = db.BeginReadOnlyBatch();

            for (int i = 0; i < accounts; i++)
            {
                Keccak keccak = random.NextKeccak();

                if (!read.TryGet(Key.Account(keccak), out ReadOnlySpan<byte> value))
                    Assert.Fail("The account should exist");
                Assert.That(value.ToArray(), Is.EqualTo(GetData()), "The account should have data right");

                if (!read.TryGet(Key.StorageCell(NibblePath.FromKey(keccak), keccak), out value))
                    Assert.Fail("The storage cell should exist");
                Assert.That(value.ToArray(), Is.EqualTo(GetData()), "The storage cell should have data right");

                for (int j = 0; j < merkleCount; j++)
                {
                    // all the Merkle values
                    if (!read.TryGet(Key.Merkle(NibblePath.FromKey(keccak).SliceTo(j)), out value))
                        Assert.Fail("The Merkle should exist");
                    Assert.That(value.ToArray(), Is.EqualTo(GetData()),
                        $"The Merkle @{j} of {i}th account should have data right");
                }
            }
        }

        byte[] GetData()
        {
            byte[] bytes = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, counter);
            counter++;
            return bytes;
        }
    }

    [Test]
    public async Task DeleteByPrefix_Accounts()
    {
        using PagedDb db = PagedDb.NativeMemoryDb(16 * Mb, 2);

        Keccak keccak0 = Values.Key0;
        Keccak keccak1 = Values.Key0;
        Keccak keccak2 = Values.Key0;
        Keccak keccak3 = Values.Key0;
        Keccak prefix = Values.Key0;

        keccak0.BytesAsSpan[^1] = 0x01;
        keccak1.BytesAsSpan[^1] = 0x02;
        keccak2.BytesAsSpan[^1] = 0x03;
        keccak3.BytesAsSpan[^1] = 0x04;
        prefix.BytesAsSpan[^1] = 0x00;

        // Set data
        using IBatch batch = db.BeginNextBatch();

        byte[] value = [1];
        batch.SetRaw(Key.Account(keccak0), value);
        batch.SetRaw(Key.Account(keccak1), value);
        batch.SetRaw(Key.Account(keccak2), value);
        batch.SetRaw(Key.Account(keccak3), value);

        await batch.Commit(CommitOptions.FlushDataAndRoot);
        using IReadOnlyBatch beforeDelete = db.BeginReadOnlyBatch();

        // Delete by prefix
        using IBatch batch2 = db.BeginNextBatch();
        batch2.DeleteByPrefix(Key.Merkle(NibblePath.FromKey(prefix).SliceTo(NibblePath.KeccakNibbleCount - 1)));
        await batch2.Commit(CommitOptions.FlushDataAndRoot);

        using IReadOnlyBatch read = db.BeginReadOnlyBatch();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read.TryGet(Key.Account(keccak0), out _), Is.False);
            Assert.That(read.TryGet(Key.Account(keccak1), out _), Is.False);
            Assert.That(read.TryGet(Key.Account(keccak2), out _), Is.False);
            Assert.That(read.TryGet(Key.Account(keccak3), out _), Is.False);
            Assert.That(beforeDelete.TryGet(Key.Account(keccak0), out _), Is.True);
            Assert.That(beforeDelete.TryGet(Key.Account(keccak1), out _), Is.True);
            Assert.That(beforeDelete.TryGet(Key.Account(keccak2), out _), Is.True);
            Assert.That(beforeDelete.TryGet(Key.Account(keccak3), out _), Is.True);
        }
    }

    [Test]
    public async Task DeleteByPrefix_Storage()
    {
        using PagedDb db = PagedDb.NativeMemoryDb(16 * Mb, 2);

        Keccak account = Values.Key2;

        Keccak keccak0 = Values.Key0;
        Keccak keccak1 = Values.Key0;
        Keccak keccak2 = Values.Key0;
        Keccak keccak3 = Values.Key0;
        Keccak prefix = Values.Key0;

        keccak0.BytesAsSpan[^1] = 0x01;
        keccak1.BytesAsSpan[^1] = 0x02;
        keccak2.BytesAsSpan[^1] = 0x03;
        keccak3.BytesAsSpan[^1] = 0x04;
        prefix.BytesAsSpan[^1] = 0x00;

        // Set data
        using IBatch batch = db.BeginNextBatch();

        byte[] value = [1];
        batch.SetRaw(Key.StorageCell(NibblePath.FromKey(account), keccak0), value);
        batch.SetRaw(Key.StorageCell(NibblePath.FromKey(account), keccak1), value);
        batch.SetRaw(Key.StorageCell(NibblePath.FromKey(account), keccak2), value);
        batch.SetRaw(Key.StorageCell(NibblePath.FromKey(account), keccak3), value);

        await batch.Commit(CommitOptions.FlushDataAndRoot);
        using IReadOnlyBatch beforeDelete = db.BeginReadOnlyBatch();

        // Delete by prefix
        using IBatch batch2 = db.BeginNextBatch();
        batch2.DeleteByPrefix(Key.Raw(NibblePath.FromKey(account), DataType.Merkle,
            NibblePath.FromKey(prefix).SliceTo(NibblePath.KeccakNibbleCount - 1)));
        await batch2.Commit(CommitOptions.FlushDataAndRoot);

        using IReadOnlyBatch read = db.BeginReadOnlyBatch();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read.TryGet(Key.StorageCell(NibblePath.FromKey(account), keccak0), out _), Is.False);
            Assert.That(read.TryGet(Key.StorageCell(NibblePath.FromKey(account), keccak1), out _), Is.False);
            Assert.That(read.TryGet(Key.StorageCell(NibblePath.FromKey(account), keccak2), out _), Is.False);
            Assert.That(read.TryGet(Key.StorageCell(NibblePath.FromKey(account), keccak3), out _), Is.False);
            Assert.That(beforeDelete.TryGet(Key.StorageCell(NibblePath.FromKey(account), keccak0), out _), Is.True);
            Assert.That(beforeDelete.TryGet(Key.StorageCell(NibblePath.FromKey(account), keccak1), out _), Is.True);
            Assert.That(beforeDelete.TryGet(Key.StorageCell(NibblePath.FromKey(account), keccak2), out _), Is.True);
            Assert.That(beforeDelete.TryGet(Key.StorageCell(NibblePath.FromKey(account), keccak3), out _), Is.True);
        }
    }

    [Test]
    public async Task Multiple_storages_per_commit()
    {
        Keccak account = Values.Key0;

        const int accounts = 512 * 1024;
        const int size = 10_000;

        using PagedDb db = PagedDb.NativeMemoryDb(8 * 1024 * Mb, 2);

        byte[] value = [13];

        using IBatch batch = db.BeginNextBatch();

        // First, set the account that will be tested later
        batch.SetRaw(Key.StorageCell(NibblePath.FromKey(account), Keccak.EmptyTreeHash), value);

        // Then flood the id cache so that the account is flushed down
        for (int i = 0; i < accounts; i++)
        {
            Keccak keccak = default;
            BinaryPrimitives.WriteInt32LittleEndian(keccak.BytesAsSpan, i);

            batch.SetRaw(Key.StorageCell(NibblePath.FromKey(keccak), keccak), value);
        }

        await batch.Commit(CommitOptions.FlushDataAndRoot);

        await InsertLoadsOfStorages(db, account);

        static async Task InsertLoadsOfStorages(IDb db, Keccak account)
        {
            byte[] value = new byte[4];

            using IBatch batch2 = db.BeginNextBatch();

            // Now try to store many
            for (int i = 0; i < size; i++)
            {
                Keccak keccak = default;
                BinaryPrimitives.WriteInt32LittleEndian(keccak.BytesAsSpan, i);
                BinaryPrimitives.WriteInt32LittleEndian(value, i);

                batch2.SetRaw(Key.StorageCell(NibblePath.FromKey(account), keccak), value);
            }

            await batch2.Commit(CommitOptions.FlushDataAndRoot);
        }
    }

    [Test]
    [Ignore("Heavily dependent on the WriteId method in the root. For smaller networks it might be much better, " +
            "clustering the values in smaller number of buckets")]
    [Explicit("long running")]
    public async Task FanOut()
    {
        // To saturate fan out
        const int size = DbAddressList.Of1024.Count * DbAddressList.Of1024.Count;

        using PagedDb db = PagedDb.NativeMemoryDb(8 * 1024 * Mb, 2);

        byte[] value = new byte[4];

        using IBatch batch = db.BeginNextBatch();

        for (int i = 0; i < size; i++)
        {
            Keccak keccak = default;
            BinaryPrimitives.WriteInt32LittleEndian(keccak.BytesAsSpan, i);
            BinaryPrimitives.WriteInt32LittleEndian(value, i);

            batch.SetRaw(Key.StorageCell(NibblePath.FromKey(keccak), keccak), value);
        }

        await batch.Commit(CommitOptions.FlushDataAndRoot);

        AssertData(db);
        return;

        static void AssertData(PagedDb db)
        {
            byte[] expected = new byte[4];

            using IReadOnlyBatch read = db.BeginReadOnlyBatch();

            for (int i = 0; i < size; i++)
            {
                Keccak keccak = default;
                BinaryPrimitives.WriteInt32LittleEndian(keccak.BytesAsSpan, i);
                BinaryPrimitives.WriteInt32LittleEndian(expected, i);

                if (!read.TryGet(Key.StorageCell(NibblePath.FromKey(keccak), keccak), out ReadOnlySpan<byte> actual))
                    Assert.Fail($"The storage cell {i} should exist");
                Assert.That(actual.ToArray(), Is.EqualTo(expected));
            }
        }
    }

    [Test]
    public async Task HasState_queries_state_properly()
    {
        Keccak keccak = Values.Key0;

        const byte historyDepth = 16;
        const byte spins = 51;

        using PagedDb db = PagedDb.NativeMemoryDb(2 * Mb, historyDepth);

        byte[] value = new byte[4];

        for (uint i = 1; i < spins; i++)
        {
            using IBatch batch = db.BeginNextBatch();

            Keccak hash = default;
            BinaryPrimitives.WriteUInt32LittleEndian(hash.BytesAsSpan, i);
            BinaryPrimitives.WriteUInt32LittleEndian(value, i);

            batch.SetRaw(Key.StorageCell(NibblePath.FromKey(keccak), keccak), value);
            batch.SetMetadata(i, hash);
            await batch.Commit(CommitOptions.FlushDataAndRoot);
        }

        for (uint i = 1; i < spins; i++)
        {
            Keccak hash = default;
            BinaryPrimitives.WriteUInt32LittleEndian(hash.BytesAsSpan, i);

            bool shouldHaveState = i >= spins - historyDepth;
            Assert.That(db.HasState(hash), Is.EqualTo(shouldHaveState), $"Failed at {i}th spin");

            if (shouldHaveState)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(value, i);

                using IReadOnlyBatch read = db.BeginReadOnlyBatch(hash);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(read.Metadata.StateHash, Is.EqualTo(hash));
                    Assert.That(read.Metadata.BlockNumber, Is.EqualTo(i));
                }

                AssertRead(value, keccak, read);
            }
        }

        return;

        static void AssertRead(byte[] bytes, in Keccak key, IReadOnlyBatch read)
        {
            if (!read.TryGet(Key.StorageCell(NibblePath.FromKey(key), key), out ReadOnlySpan<byte> existing))
                Assert.Fail("The storage cell should exist");
            Assert.That(existing.ToArray(), Is.EqualTo(bytes));
        }
    }

    [Test]
    public async Task Snapshotting()
    {
        const byte historyDepth = 16;

        byte[] data = new byte[32];

        using PagedDb db = PagedDb.NativeMemoryDb(32 * Mb, historyDepth);

        for (uint i = 0; i < historyDepth * 2; i++)
        {
            Keccak keccak = default;

            BinaryPrimitives.WriteUInt32LittleEndian(keccak.BytesAsSpan, i);

            using IBatch batch = db.BeginNextBatch();

            // account first & data
            batch.SetRaw(Key.Account(keccak), data);
            batch.SetRaw(Key.StorageCell(NibblePath.FromKey(keccak), keccak), data);

            batch.SetMetadata(i, Keccak.Compute(keccak.BytesAsSpan));

            await batch.Commit(CommitOptions.FlushDataAndRoot);
        }

        // Asserts
        IReadOnlyBatch[] snapshot = db.SnapshotAll();

        Assert.That(snapshot, Has.Length.EqualTo(historyDepth));

        Array.Sort(snapshot, static (a, b) => a.Metadata.BlockNumber.CompareTo(b.Metadata.BlockNumber));
        uint start = snapshot[0].Metadata.BlockNumber;

        for (uint i = 0; i < historyDepth; i++)
        {
            Assert.That(snapshot[i].Metadata.BlockNumber, Is.EqualTo(start + i));
        }

        foreach (IReadOnlyBatch batch in snapshot)
        {
            batch.Dispose();
        }
    }
}
