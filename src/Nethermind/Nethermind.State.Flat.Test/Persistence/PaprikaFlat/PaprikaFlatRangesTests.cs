// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.PaprikaFlat;
using NUnit.Framework;
using Paprika;
using Paprika.Data;
using Paprika.Store;
using static Nethermind.State.Flat.Test.Persistence.PaprikaFlat.PaprikaFlatTestDb;

namespace Nethermind.State.Flat.Test.Persistence.PaprikaFlat;

[TestFixture]
public class PaprikaFlatRangesTests
{
    private static readonly byte[] Contracts = [0x44, 0x55, 0x66, 0x77, 0x88];

    private const int ModelKeys = 2_000;
    private const int ClusterSize = 128;
    private const int ModelRounds = 8;
    private const int RangeDeletesPerBuffer = 4;
    private const int WritesPerBuffer = 64;
    private const int DenseKeys = 20_000;

    /// <summary>About the size of a stored account, so pages fill and split as they do for real accounts.</summary>
    private const int AccountValueSize = 80;

    private static IEnumerable<TestCaseData> AccountRanges()
    {
        yield return new TestCaseData(Path(0x00), Path(0xff), new byte[] { 0x11, 0x22, 0x33, 0x44 }).SetName("Whole range");
        yield return new TestCaseData(Path(0x11), Path(0x33), new byte[] { 0x11, 0x22 }).SetName("End is exclusive");
        yield return new TestCaseData(Path(0x11), Path(0x33, last: 0xff), new byte[] { 0x11, 0x22 }).SetName("End is compared on its first 20 bytes");
        yield return new TestCaseData(Path(0x11), Path(0x33, twentieth: 0x01), new byte[] { 0x11, 0x22, 0x33 }).SetName("End past the account key");
        yield return new TestCaseData(Path(0x22, last: 0xff), Path(0x44), new byte[] { 0x22, 0x33 }).SetName("Start is compared on its first 20 bytes");
        yield return new TestCaseData(Path(0x00), Path(0x00), Array.Empty<byte>()).SetName("Zero end");
    }

    [TestCaseSource(nameof(AccountRanges))]
    public void Account_iterator_end_is_exclusive_and_paths_sharing_20_bytes_are_one_account(ValueHash256 start, ValueHash256 end, byte[] expected)
    {
        using PaprikaFlatTestDb db = new();
        db.Write(static batch =>
        {
            batch.SetAccount(Path(0x11), Bytes.FromHexString("0x11"));
            batch.SetAccount(Path(0x22, last: 0x01), Bytes.FromHexString("0x21"));
            batch.SetAccount(Path(0x22, last: 0x02), Bytes.FromHexString("0x22"));
            batch.SetAccount(Path(0x33), Bytes.FromHexString("0x33"));
            batch.SetAccount(Path(0x44), Bytes.FromHexString("0x44"));
        });

        using IVisitableReadOnlyBatch reader = db.BeginReadOnlyBatch();
        Assert.That(Accounts(reader, start, end), Is.EqualTo(expected.Select(static first => (Path(first), $"0x{first:x2}"))));
    }

    [TestCase((byte)0x01, (byte)0x02, new byte[] { 0x01, 0x02 })]
    [TestCase((byte)0x02, (byte)0x03, new byte[] { 0x02, 0x03 })]
    [TestCase((byte)0x02, (byte)0x02, new byte[] { 0x02 })]
    [TestCase((byte)0x03, (byte)0x01, new byte[0])]
    public void Storage_iterator_end_is_inclusive_and_values_are_unwrapped(byte start, byte end, byte[] expected)
    {
        using PaprikaFlatTestDb db = new();
        db.Write(static batch =>
        {
            for (byte slot = 1; slot <= 3; slot++) batch.SetStorage(Path(0x22, last: 0x01), Path(slot), (UInt256)(slot * 0x0101));
        });

        // Read under another path sharing the first 20 bytes, as callers pass the account iterator's key back.
        using IVisitableReadOnlyBatch reader = db.BeginReadOnlyBatch();
        Assert.That(Slots(reader, Path(0x22), Path(start), Path(end)), Is.EqualTo(expected.Select(static slot => (Path(slot), $"0x{slot:x2}{slot:x2}"))));
    }

    [Test]
    public void Whole_storage_deletes_drop_every_slot_including_storage_without_an_account()
    {
        using PaprikaFlatTestDb db = new();
        db.Write(static batch =>
        {
            // 0x66 is the only contract with an account.
            batch.SetAccount(Path(0x66), Bytes.FromHexString("0x66"));
            foreach (byte contract in Contracts)
            {
                for (byte slot = 1; slot <= 3; slot++) batch.SetStorage(Path(contract), Path(slot), (UInt256)slot);
            }
        });
        db.Write(static batch =>
        {
            batch.SelfDestructRange(Path(0x55, last: 0xff), Path(0x66));
            batch.DeleteStorageRange(Path(0x77), ValueKeccak.Zero, ValueKeccak.MaxValue);
        });

        using IVisitableReadOnlyBatch reader = db.BeginReadOnlyBatch();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Contracts.Select(contract => Slots(reader, Path(contract)).Count), Is.EqualTo(new[] { 3, 0, 0, 0, 3 }));
            Assert.That(Accounts(reader, ValueKeccak.Zero, ValueKeccak.MaxValue), Is.EqualTo(new[] { (Path(0x66), "0x66") }));
        }
    }

    private static IEnumerable<TestCaseData> AccountRangeDrops()
    {
        yield return new TestCaseData(ValueKeccak.Zero, ValueKeccak.MaxValue, new[] { "11", "12", "AB" }, 0, Array.Empty<byte>()).SetName("Whole account space");
        yield return new TestCaseData(Path(0x10), new ValueHash256("0x1fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"), new[] { "11", "12" }, 0, new byte[] { 0xab }).SetName("One-nibble block");
        yield return new TestCaseData(Path(0x11, twentieth: 0x05), Path(0xab), new[] { "12" }, 1, new byte[] { 0x11 }).SetName("Unaligned bounds");
    }

    /// <remarks>
    /// A prefix shorter than two nibbles would reach the state root page's own map, which holds the markers; the page does
    /// not delete in it today, so only the prefixes deleted show that none is.
    /// </remarks>
    [TestCaseSource(nameof(AccountRangeDrops))]
    public void Account_range_drops_only_blocks_of_at_least_two_nibbles_that_hold_accounts(ValueHash256 from, ValueHash256 to, string[] drops, int keyDeletes, byte[] remaining)
    {
        using PaprikaFlatTestDb db = new();
        using (PaprikaWriteBuffer seed = new(StateId.Sync, StateId.Sync))
        {
            PaprikaFlatWriteBatch batch = new(seed);
            foreach (byte first in new byte[] { 0x11, 0x12, 0xab }) batch.SetAccount(Path(first), Bytes.FromHexString("0x01"));
            seed.Metadata.Set(Bytes.FromHexString("0x01"), Bytes.FromHexString("0x02"));
            db.Commit(seed);
        }

        using PaprikaWriteBuffer buffer = new(StateId.Sync, StateId.Sync);
        buffer.DeleteAccountRange(from, to);
        RecordingBatch recording;
        using (IVisitableReadOnlyBatch preBatch = db.BeginReadOnlyBatch())
        using (recording = new RecordingBatch(db.BeginNextBatch()))
        {
            PaprikaWriteBuffer.ApplyTo([buffer], preBatch, recording);
            recording.Commit(CommitOptions.FlushDataAndRoot).AsTask().GetAwaiter().GetResult();
        }

        using IVisitableReadOnlyBatch reader = db.BeginReadOnlyBatch();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(recording.Prefixes, Is.EqualTo(drops));
            Assert.That(recording.KeyDeletes, Is.EqualTo(keyDeletes));
            Assert.That(Accounts(reader, ValueKeccak.Zero, ValueKeccak.MaxValue).Select(static entry => entry.Key), Is.EqualTo(remaining.Select(static first => Path(first))));
            Assert.That(PaprikaFlatMetadata.Read(reader).Encode(), Is.EqualTo(Bytes.FromHexString("0x010101010102")));
        }
    }

    /// <remarks>
    /// The keys come in clusters sharing a prefix of any length, and every round writes them all again first, so blocks of
    /// every depth hold keys. The bounds are the extremes, keys, keys with another last nibble, aligned on a block of any
    /// depth, or random, and an account bound has random bytes past the account key. A group applies its range deletes
    /// before any of its writes, so the model applies a range delete at once and defers the writes.
    /// </remarks>
    [Test]
    public void Range_deletes_match_key_by_key_deletes_of_a_dictionary_model([Range(1, 10)] int seed)
    {
        Random random = new(seed);
        ValueHash256[] accountKeys = ClusteredKeys(random, PaprikaFlatKeys.AccountKeyLength);
        ValueHash256[] slotKeys = ClusteredKeys(random, ValueHash256.MemorySize);
        // They share one storage subtree; the last has no storage, so its range deletes find no contract.
        ValueHash256[] contracts = [.. accountKeys.Take(4)];
        Dictionary<ValueHash256, string> accounts = [];
        Dictionary<ValueHash256, Dictionary<ValueHash256, string>> storages = contracts.ToDictionary(static contract => contract, static _ => new Dictionary<ValueHash256, string>());

        using PaprikaFlatTestDb db = new();
        for (int round = 0; round < ModelRounds; round++)
        {
            using (PaprikaWriteBuffer seedBuffer = new(StateId.Sync, StateId.Sync))
            {
                foreach (ValueHash256 account in accountKeys) PutAccount(seedBuffer, account, RandomBytes(random, AccountValueSize), accounts).Invoke();
                for (int contract = 0; contract < contracts.Length - 1; contract++)
                {
                    for (int i = 0; i < slotKeys.Length; i += contract + 1) PutSlot(seedBuffer, contracts[contract], slotKeys[i], RandomBytes(random, ValueHash256.MemorySize), storages[contracts[contract]]).Invoke();
                }

                seedBuffer.Metadata.Set(Bytes.FromHexString("0x01"), Bytes.FromHexString("0x0102"));
                db.Commit(seedBuffer);
            }

            using PaprikaWriteBuffer first = new(StateId.Sync, StateId.Sync);
            using PaprikaWriteBuffer second = new(StateId.Sync, StateId.Sync);
            PaprikaWriteBuffer[] group = round % 2 == 0 ? [first] : [first, second];
            List<Action> writes = [];
            if (round == ModelRounds - 1)
            {
                first.DeleteAccountRange(ValueKeccak.Zero, ValueKeccak.MaxValue);
                accounts.Clear();
            }

            foreach (PaprikaWriteBuffer buffer in group)
            {
                for (int i = 0; i < RangeDeletesPerBuffer; i++)
                {
                    ValueHash256 contract = contracts[random.Next(contracts.Length)];
                    bool storage = random.Next(2) == 0;
                    (ValueHash256 from, ValueHash256 to) = storage
                        ? RandomRange(random, slotKeys, ValueHash256.MemorySize)
                        : RandomRange(random, accountKeys, PaprikaFlatKeys.AccountKeyLength);
                    if (storage) buffer.DeleteStorageRange(contract, from, to);
                    else buffer.DeleteAccountRange(from, to);
                    RemoveRange(storage ? storages[contract] : accounts, from, to, storage ? ValueHash256.MemorySize : PaprikaFlatKeys.AccountKeyLength);
                }

                for (int i = 0; i < WritesPerBuffer; i++)
                {
                    ValueHash256 account = accountKeys[random.Next(accountKeys.Length)];
                    ValueHash256 contract = contracts[random.Next(contracts.Length)];
                    ValueHash256 slot = slotKeys[random.Next(slotKeys.Length)];
                    byte[] value = RandomBytes(random, random.Next(2) == 0 ? AccountValueSize : ValueHash256.MemorySize);
                    writes.Add(random.Next(4) switch
                    {
                        0 => PutAccount(buffer, account, value, accounts),
                        1 => RemoveAccount(buffer, account, accounts),
                        2 => PutSlot(buffer, contract, slot, value[..ValueHash256.MemorySize], storages[contract]),
                        _ => RemoveSlot(buffer, contract, slot, storages[contract]),
                    });
                }
            }

            foreach (Action write in writes) write();
            db.Commit(group);

            using IVisitableReadOnlyBatch reader = db.BeginReadOnlyBatch();
            reader.VerifyNoPagesMissing();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(AllAccounts(reader), Is.EqualTo(Sorted(accounts)), $"round {round}: accounts");
                foreach (ValueHash256 contract in contracts)
                {
                    Assert.That(Slots(reader, contract), Is.EqualTo(Sorted(storages[contract])), $"round {round}: slots of {contract}");
                }

                Assert.That(PaprikaFlatMetadata.Read(reader).Encode(), Is.EqualTo(Bytes.FromHexString("0x01010101020102")), $"round {round}: markers");
            }
        }
    }

    /// <remarks>
    /// The keys share their first byte and the range covers half of them, so it spans a deep subtree: deleted key by key,
    /// every page of it is copied in the batch; dropped, the pages are released and only those above its blocks copied.
    /// </remarks>
    [Test]
    public void Large_aligned_range_is_dropped_without_copying_its_pages_and_keeps_later_writes([Values] bool storage)
    {
        int keyLength = storage ? ValueHash256.MemorySize : PaprikaFlatKeys.AccountKeyLength;
        ValueHash256 contract = Path(0x99);
        ValueHash256 from = new("0xab00000000000000000000000000000000000000000000000000000000000000");
        ValueHash256 to = new("0xab7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");
        ValueHash256 written = Path(0xab, twentieth: 0x01, last: 0x01);
        Random random = new(42);
        ValueHash256[] keys = [.. Enumerable.Range(0, DenseKeys).Select(_ => WithFirstByte(RandomKey(random, keyLength), 0xab))];
        byte[] value = RandomBytes(random, AccountValueSize);

        using PaprikaFlatTestDb blocks = new();
        using PaprikaFlatTestDb perKey = new();
        foreach (PaprikaFlatTestDb db in new[] { blocks, perKey })
        {
            db.Write(batch =>
            {
                foreach (ValueHash256 key in keys) Set(batch, key);
            });
        }

        // Neither range holds a key, so neither copies a page. Both files write them, so both reuse pages alike afterwards.
        uint beforeEmpty = blocks.NextFreePage;
        foreach (PaprikaFlatTestDb db in new[] { blocks, perKey })
        {
            db.Write(batch =>
            {
                Delete(batch, new("0x1200000000000000000000000000000000000000000000000000000000000000"), new("0x12ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"));
                Delete(batch, new("0xab00000000000000000000000000000000000000000000000000000000000001"), new("0xab000000ffffffffffffffffffffffffffffffffffffffffffffffffffffffff"));
            });
        }

        uint emptyGrowth = blocks.NextFreePage - beforeEmpty;

        uint beforeBlocks = blocks.NextFreePage;
        blocks.Write(batch =>
        {
            Delete(batch, from, to);
            Set(batch, written);
        });
        uint blockGrowth = blocks.NextFreePage - beforeBlocks;

        uint beforePerKey = perKey.NextFreePage;
        perKey.Write(batch =>
        {
            foreach (ValueHash256 key in keys)
            {
                if (key.Bytes[..keyLength].SequenceCompareTo(to.Bytes[..keyLength]) <= 0) Remove(batch, key);
            }

            Set(batch, written);
        });
        uint perKeyGrowth = perKey.NextFreePage - beforePerKey;

        using IVisitableReadOnlyBatch blocksReader = blocks.BeginReadOnlyBatch();
        using IVisitableReadOnlyBatch perKeyReader = perKey.BeginReadOnlyBatch();
        blocksReader.VerifyNoPagesMissing();
        perKeyReader.VerifyNoPagesMissing();
        List<(ValueHash256 Key, string Value)> remaining = storage ? Slots(blocksReader, contract) : AllAccounts(blocksReader);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(remaining, Is.EqualTo(storage ? Slots(perKeyReader, contract) : AllAccounts(perKeyReader)));
            Assert.That(remaining.Select(static entry => entry.Key), Does.Contain(storage ? written : PaprikaFlatKeys.StorageAccount(written.Bytes)));
            Assert.That(remaining, Has.Count.EqualTo(keys.Count(key => key.Bytes[..keyLength].SequenceCompareTo(to.Bytes[..keyLength]) > 0) + 1));
            Assert.That(emptyGrowth, Is.Zero);
            Assert.That(blockGrowth, Is.LessThan(perKeyGrowth / 10));
        }

        void Set(PaprikaFlatWriteBatch batch, in ValueHash256 key)
        {
            if (storage) batch.SetStorage(contract, key, UInt256.One);
            else batch.SetAccount(key, value);
        }

        void Remove(PaprikaFlatWriteBatch batch, in ValueHash256 key)
        {
            if (storage) batch.SetStorage(contract, key, null);
            else batch.RemoveAccount(key);
        }

        void Delete(PaprikaFlatWriteBatch batch, in ValueHash256 start, in ValueHash256 end)
        {
            if (storage) batch.DeleteStorageRange(contract, start, end);
            else batch.DeleteAccountRange(start, end);
        }
    }

    [Test]
    public void Values_round_trip_and_a_zero_slot_is_stored_non_empty_and_reads_as_present()
    {
        Account account = new(5, 1_000);
        ValueHash256 storageAccount = PaprikaFlatKeys.StorageAccount(TestItem.AddressA.ToAccountPath.Bytes);
        ValueHash256 zeroSlot = Path(0x01);
        ValueHash256 removedSlot = Path(0x02);
        ValueHash256 encodedSlot = Path(0x03);

        using PaprikaFlatTestDb db = new();
        db.Write(batch =>
        {
            BasePersistence.ToHashedWriteBatch<PaprikaFlatWriteBatch> hashed = new(batch);
            hashed.SetAccount(TestItem.AddressA, account);
            batch.SetStorage(storageAccount, zeroSlot, UInt256.Zero);
            batch.SetStorage(storageAccount, removedSlot, (UInt256)7);
            // Trailing bytes after the RLP item are not stored.
            batch.SetStorageEncoded(storageAccount, encodedSlot, Bytes.FromHexString("0x820a0bff"));
        });
        db.Write(batch => batch.SetStorage(storageAccount, removedSlot, null));

        using IVisitableReadOnlyBatch reader = db.BeginReadOnlyBatch();
        BasePersistence.ToHashedFlatReader<PaprikaFlatReader> flat = new(new PaprikaFlatReader(reader));
        Account? read = flat.GetAccount(TestItem.AddressA);
        UInt256 zero = UInt256.One;
        UInt256 removed = UInt256.Zero;
        reader.TryGet(PaprikaFlatKeys.Slot(storageAccount.Bytes, zeroSlot.Bytes), out ReadOnlySpan<byte> storedZero);
        string storedZeroHex = storedZero.ToArray().ToHexString(true);
        reader.TryGet(PaprikaFlatKeys.Slot(storageAccount.Bytes, encodedSlot.Bytes), out ReadOnlySpan<byte> storedEncoded);
        string storedEncodedHex = storedEncoded.ToArray().ToHexString(true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read!.Nonce, Is.EqualTo(5UL));
            Assert.That(read.Balance, Is.EqualTo((UInt256)1_000));
            Assert.That(flat.GetAccount(TestItem.AddressB), Is.Null);
            Assert.That(flat.SupportsPrefetch, Is.False);
            Assert.That(storedZeroHex, Is.EqualTo("0x00"));
            Assert.That(storedEncodedHex, Is.EqualTo("0x820a0b"));
            Assert.That(flat.TryGetSlotRaw(storageAccount, zeroSlot, ref zero), Is.True);
            Assert.That(zero, Is.EqualTo(UInt256.Zero));
            Assert.That(flat.TryGetSlotRaw(storageAccount, removedSlot, ref removed), Is.False);
            Assert.That(Slots(reader, storageAccount), Is.EqualTo(new[] { (zeroSlot, "0x00"), (encodedSlot, "0x0a0b") }));
        }
    }

    /// <summary>A batch that records the prefixes it deletes and counts the keys it deletes one by one.</summary>
    private sealed class RecordingBatch(IBatch inner) : IBatch
    {
        public List<string> Prefixes { get; } = [];

        public int KeyDeletes { get; private set; }

        public Metadata Metadata => inner.Metadata;

        public bool TryGet(scoped in Key key, out ReadOnlySpan<byte> result) => inner.TryGet(key, out result);

        public void VerifyNoPagesMissing() => inner.VerifyNoPagesMissing();

        public void SetMetadata(uint blockNumber, in Paprika.Crypto.Keccak blockHash) => inner.SetMetadata(blockNumber, blockHash);

        public void SetRaw(in Key key, ReadOnlySpan<byte> rawData)
        {
            if (rawData.IsEmpty) KeyDeletes++;
            inner.SetRaw(key, rawData);
        }

        public void Destroy(in NibblePath account) => inner.Destroy(account);

        public void DeleteByPrefix(in Key prefix)
        {
            Prefixes.Add(prefix.Path.ToString());
            inner.DeleteByPrefix(prefix);
        }

        public void Clear() => inner.Clear();

        public ValueTask Commit(CommitOptions options) => inner.Commit(options);

        public void VerifyDbPagesOnCommit() => inner.VerifyDbPagesOnCommit();

        public void Dispose() => inner.Dispose();
    }

    /// <summary>Writes the account to <paramref name="buffer"/> and returns the matching update of <paramref name="model"/>.</summary>
    private static Action PutAccount(PaprikaWriteBuffer buffer, ValueHash256 account, byte[] value, Dictionary<ValueHash256, string> model)
    {
        buffer.SetAccount(account, value);
        return () => model[account] = value.ToHexString(true);
    }

    private static Action RemoveAccount(PaprikaWriteBuffer buffer, ValueHash256 account, Dictionary<ValueHash256, string> model)
    {
        buffer.RemoveAccount(account);
        return () => model.Remove(account);
    }

    private static Action PutSlot(PaprikaWriteBuffer buffer, ValueHash256 contract, ValueHash256 slot, byte[] value, Dictionary<ValueHash256, string> model)
    {
        buffer.SetSlot(contract, slot, Rlp.Encode(value).Bytes);
        return () => model[slot] = value.ToHexString(true);
    }

    private static Action RemoveSlot(PaprikaWriteBuffer buffer, ValueHash256 contract, ValueHash256 slot, Dictionary<ValueHash256, string> model)
    {
        buffer.RemoveSlot(contract, slot);
        return () => model.Remove(slot);
    }

    private static void RemoveRange(Dictionary<ValueHash256, string> model, ValueHash256 from, ValueHash256 to, int keyLength)
    {
        foreach (ValueHash256 key in model.Keys.ToArray())
        {
            if (key.Bytes[..keyLength].SequenceCompareTo(from.Bytes[..keyLength]) >= 0 && key.Bytes[..keyLength].SequenceCompareTo(to.Bytes[..keyLength]) <= 0) model.Remove(key);
        }
    }

    private static List<(ValueHash256 Key, string Value)> Sorted(Dictionary<ValueHash256, string> model) =>
        [.. model.OrderBy(static entry => entry.Key).Select(static entry => (entry.Key, entry.Value))];

    /// <summary>Every account, the extreme keys included, with its key zero-padded.</summary>
    private static List<(ValueHash256 Key, string Value)> AllAccounts(IVisitableReadOnlyBatch reader)
    {
        ValueHash256 first = Filled(default, 0, PaprikaFlatKeys.AccountKeyLength, 0x00);
        ValueHash256 last = Filled(default, 0, PaprikaFlatKeys.AccountKeyLength, 0xff);
        using RangeIterator accounts = reader.IterateState(first.Bytes[..PaprikaFlatKeys.AccountKeyLength], last.Bytes[..PaprikaFlatKeys.AccountKeyLength]);
        List<(ValueHash256, string)> entries = [];
        while (accounts.MoveNext()) entries.Add((PaprikaFlatKeys.StorageAccount(accounts.CurrentKey), accounts.CurrentValue.ToArray().ToHexString(true)));
        return entries;
    }

    /// <summary>Keys of <paramref name="keyLength"/> bytes, zero-padded, in clusters sharing a prefix of any length, and the extreme keys.</summary>
    private static ValueHash256[] ClusteredKeys(Random random, int keyLength)
    {
        List<ValueHash256> keys = [Filled(default, 0, keyLength, 0x00), Filled(default, 0, keyLength, 0xff)];
        while (keys.Count < ModelKeys)
        {
            ValueHash256 cluster = RandomKey(random, keyLength);
            // Half the clusters differ in their last nibbles only, so the blocks of the last nibble hold keys.
            int sharedNibbles = random.Next(2) == 0 ? random.Next(keyLength * 2) : keyLength * 2 - random.Next(1, 4);
            for (int i = 0; i < ClusterSize; i++)
            {
                ValueHash256 key = RandomKey(random, keyLength);
                cluster.Bytes[..(sharedNibbles / 2)].CopyTo(key.BytesAsSpan);
                if (sharedNibbles % 2 == 1) key.BytesAsSpan[sharedNibbles / 2] = (byte)(cluster.Bytes[sharedNibbles / 2] & 0xf0 | key.Bytes[sharedNibbles / 2] & 0x0f);
                keys.Add(key);
            }

            keys = [.. keys.Distinct()];
        }

        return [.. keys];
    }

    /// <summary>
    /// A range that is a single key, differs in the last nibble only, ends with the block of a random depth its start is in,
    /// or is between two random bounds; rarely inverted. The bytes of the bounds past <paramref name="keyLength"/> are random.
    /// </summary>
    private static (ValueHash256 From, ValueHash256 To) RandomRange(Random random, ValueHash256[] keys, int keyLength)
    {
        ValueHash256 from = RandomBound(random, keys, keyLength, 0x00);
        ValueHash256 to = random.Next(4) switch
        {
            0 => from,
            1 => WithLastNibble(from, keyLength, random.Next(16)),
            2 => Filled(from, random.Next(keyLength * 2 + 1), keyLength, 0xff),
            _ => RandomBound(random, keys, keyLength, 0xff),
        };
        random.NextBytes(from.BytesAsSpan[keyLength..]);
        random.NextBytes(to.BytesAsSpan[keyLength..]);
        return from.CompareTo(to) <= 0 || random.Next(16) == 0 ? (from, to) : (to, from);
    }

    /// <summary>An extreme, a key, a key with another last nibble, a key's prefix filled with <paramref name="fill"/>'s nibble, or a random bound.</summary>
    private static ValueHash256 RandomBound(Random random, ValueHash256[] keys, int keyLength, byte fill)
    {
        ValueHash256 key = keys[random.Next(keys.Length)];
        return random.Next(6) switch
        {
            0 => Filled(default, 0, keyLength, 0x00),
            1 => Filled(default, 0, keyLength, 0xff),
            2 => key,
            3 => WithLastNibble(key, keyLength, random.Next(16)),
            4 => Filled(key, random.Next(keyLength * 2 + 1), keyLength, fill),
            _ => RandomKey(random, keyLength),
        };
    }

    /// <summary><paramref name="key"/> with its nibbles from <paramref name="fromNibble"/> to the end of its first <paramref name="keyLength"/> bytes set to <paramref name="fill"/>'s.</summary>
    private static ValueHash256 Filled(ValueHash256 key, int fromNibble, int keyLength, byte fill)
    {
        Span<byte> bytes = key.BytesAsSpan[..keyLength];
        if (fromNibble % 2 == 1) bytes[fromNibble / 2] = (byte)(bytes[fromNibble / 2] & 0xf0 | fill & 0x0f);
        bytes[((fromNibble + 1) / 2)..].Fill(fill);
        return key;
    }

    private static ValueHash256 WithLastNibble(ValueHash256 key, int keyLength, int nibble)
    {
        key.BytesAsSpan[keyLength - 1] = (byte)(key.Bytes[keyLength - 1] & 0xf0 | nibble);
        return key;
    }

    private static ValueHash256 WithFirstByte(ValueHash256 key, byte first)
    {
        key.BytesAsSpan[0] = first;
        return key;
    }

    /// <summary>A random key of <paramref name="keyLength"/> bytes, zero-padded.</summary>
    private static ValueHash256 RandomKey(Random random, int keyLength)
    {
        ValueHash256 key = default;
        random.NextBytes(key.BytesAsSpan[..keyLength]);
        return key;
    }

    private static byte[] RandomBytes(Random random, int length)
    {
        byte[] bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }
}
