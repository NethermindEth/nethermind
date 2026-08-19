// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Persistence;

[TestFixture]
public class ArenaBasePersistenceTests
{
    // Small counts so tests cross shard boundaries: accounts by the hash's top 2 bits, storage by the top 3.
    private const int AccountShards = 4;
    private const int StorageShards = 8;

    private TempPath _dir = null!;
    private SnapshotableMemColumnsDb<FlatDbColumns> _db = null!;
    private ArenaBasePersistence _persistence = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = TempPath.GetTempDirectory();
        // neverPrune: SnapshotableMemDb tracks active snapshot versions in a set, so two snapshots taken
        // at the same version (a reader plus a fold) alias and one dispose prunes the other's view.
        _db = new SnapshotableMemColumnsDb<FlatDbColumns>(neverPrune: true);
        _persistence = NewPersistence();
    }

    [TearDown]
    public void TearDown()
    {
        _persistence.Dispose();
        _db.Dispose();
        _dir.Dispose();
    }

    private ArenaBasePersistence NewPersistence(long foldThresholdBytes = 0) =>
        new(_db, _dir.Path, new FlatDbConfig { BaseFoldThresholdBytes = foldThresholdBytes }, LimboLogs.Instance, AccountShards, StorageShards);

    private void Write(Action<IPersistence.IWriteBatch> fill)
    {
        using IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis);
        fill(batch);
    }

    private static byte[]? GetSlot(IPersistence.IPersistenceReader reader, Address address, in UInt256 slot)
    {
        SlotValue slotValue = default;
        return reader.TryGetSlot(address, in slot, ref slotValue) ? slotValue.ToEvmBytes() : null;
    }

    private Account? ReadAccount(Address address)
    {
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        return reader.GetAccount(address);
    }

    private byte[]? ReadSlot(Address address, in UInt256 slot)
    {
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        return GetSlot(reader, address, in slot);
    }

    private int OverlayEntryCount(FlatDbColumns column) =>
        _db.GetColumnDb(column).GetAll().Count();

    [TestCase(new byte[] { 0x00, 0x00 }, 1, 0)]
    [TestCase(new byte[] { 0xff, 0xff }, 1, 0)]
    [TestCase(new byte[] { 0x00, 0x00 }, 256, 0)]
    [TestCase(new byte[] { 0x42, 0x00 }, 256, 0x42)]
    [TestCase(new byte[] { 0xff, 0x00 }, 256, 0xff)]
    [TestCase(new byte[] { 0x42, 0x30 }, 4096, 0x423)]
    [TestCase(new byte[] { 0xff, 0xff }, 4096, 0xfff)]
    [TestCase(new byte[] { 0x80, 0x00 }, 4, 2)]
    [TestCase(new byte[] { 0x7f, 0xff }, 4, 1)]
    [TestCase(new byte[] { 0xe0, 0x00 }, 8, 7)]
    public void ShardRouting_UsesTopBitsOfKeyPrefix(byte[] keyPrefix, int shardCount, int expectedShard) =>
        Assert.That(BaseTableView.ShardOf(keyPrefix, shardCount), Is.EqualTo(expectedShard));

    [Test]
    public void Fold_MovesOverlayIntoShardTables_ReadsUnchanged()
    {
        Account acc0 = TestItem.GenerateIndexedAccount(0);
        Account acc1 = TestItem.GenerateIndexedAccount(1);
        Write(batch =>
        {
            batch.SetAccount(TestItem.AddressA, acc0);
            batch.SetAccount(TestItem.AddressB, acc1);
            batch.SetStorage(TestItem.AddressA, 1, SlotValue.FromSpanWithoutLeadingZero([0x11]));
            batch.SetStorage(TestItem.AddressA, UInt256.MaxValue, SlotValue.FromSpanWithoutLeadingZero([0x22, 0x33]));
        });

        _persistence.Fold();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(OverlayEntryCount(FlatDbColumns.Account), Is.Zero, "account overlay should be folded away");
            Assert.That(OverlayEntryCount(FlatDbColumns.Storage), Is.Zero, "storage overlay should be folded away");
            Assert.That(ReadAccount(TestItem.AddressA), Is.EqualTo(acc0));
            Assert.That(ReadAccount(TestItem.AddressB), Is.EqualTo(acc1));
            Assert.That(ReadSlot(TestItem.AddressA, 1), Is.EqualTo([0x11]));
            Assert.That(ReadSlot(TestItem.AddressA, UInt256.MaxValue), Is.EqualTo([0x22, 0x33]));
            Assert.That(ReadAccount(TestItem.AddressC), Is.Null);
            Assert.That(ReadSlot(TestItem.AddressA, 2), Is.Null);
        }
    }

    [Test]
    public void DeletedAccount_IsAbsent_BeforeAndAfterFold()
    {
        Write(batch =>
        {
            batch.SetAccount(TestItem.AddressA, TestItem.GenerateIndexedAccount(0));
            batch.SetAccount(TestItem.AddressB, TestItem.GenerateIndexedAccount(1));
        });
        _persistence.Fold();

        Write(batch => batch.SetAccount(TestItem.AddressA, null));

        // The tombstone shadows the base row...
        Assert.That(ReadAccount(TestItem.AddressA), Is.Null);

        // ...and the fold eliminates both without losing the deletion.
        _persistence.Fold();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ReadAccount(TestItem.AddressA), Is.Null);
            Assert.That(ReadAccount(TestItem.AddressB), Is.EqualTo(TestItem.GenerateIndexedAccount(1)));
            Assert.That(OverlayEntryCount(FlatDbColumns.Account), Is.Zero, "the tombstone must not survive the fold");
        }

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        Assert.That(reader.GetAccountRaw(TestItem.AddressA.ToAccountPath), Is.Null);
    }

    [Test]
    public void ClearedSlot_IsAbsent_ZeroSlotValue_IsPresent_AcrossFolds()
    {
        Address address = TestItem.AddressA;
        Write(batch =>
        {
            batch.SetStorage(address, 1, SlotValue.FromSpanWithoutLeadingZero([0x11]));
            batch.SetStorage(address, 2, SlotValue.FromSpanWithoutLeadingZero([0x22]));
        });
        _persistence.Fold();

        Write(batch =>
        {
            batch.SetStorage(address, 1, null); // absent
            batch.SetStorage(address, 2, SlotValue.FromSpanWithoutLeadingZero([0])); // present, zero
        });

        AssertSlots();
        _persistence.Fold();
        AssertSlots();

        void AssertSlots()
        {
            using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
            SlotValue value = default;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(reader.TryGetSlot(address, 1, ref value), Is.False, "cleared slot must read as absent");
                Assert.That(reader.TryGetSlot(address, 2, ref value), Is.True, "zero-valued slot must stay distinct from absent");
                Assert.That(value.AsReadOnlySpan.IndexOfAnyExcept((byte)0), Is.EqualTo(-1));
            }
        }
    }

    [Test]
    public void SelfDestruct_RemovesBaseOnlySlots_KeepsOtherAccounts()
    {
        Address victim = TestItem.AddressA;
        Address bystander = TestItem.AddressB;
        Write(batch =>
        {
            batch.SetAccount(victim, TestItem.GenerateIndexedAccount(0));
            batch.SetStorage(victim, 1, SlotValue.FromSpanWithoutLeadingZero([0x11]));
            batch.SetStorage(victim, UInt256.MaxValue, SlotValue.FromSpanWithoutLeadingZero([0x22]));
            batch.SetAccount(bystander, TestItem.GenerateIndexedAccount(1));
            batch.SetStorage(bystander, 1, SlotValue.FromSpanWithoutLeadingZero([0x33]));
        });
        _persistence.Fold(); // the victim's slots now live only in the base shard tables

        Write(batch => batch.SelfDestruct(victim));

        AssertState();
        _persistence.Fold();
        AssertState();
        Assert.That(OverlayEntryCount(FlatDbColumns.Storage), Is.Zero);

        void AssertState()
        {
            using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(GetSlot(reader, victim, 1), Is.Null);
                Assert.That(GetSlot(reader, victim, UInt256.MaxValue), Is.Null);
                Assert.That(GetSlot(reader, bystander, 1), Is.EqualTo([0x33]));
            }
        }
    }

    [Test]
    public void RecreatedAccount_AfterSelfDestruct_SeesOnlyNewStorage()
    {
        Address address = TestItem.AddressA;
        Write(batch =>
        {
            batch.SetAccount(address, TestItem.GenerateIndexedAccount(0));
            batch.SetStorage(address, 1, SlotValue.FromSpanWithoutLeadingZero([0x11]));
        });
        _persistence.Fold();

        Account recreated = TestItem.GenerateIndexedAccount(2);
        Write(batch =>
        {
            batch.SelfDestruct(address);
            batch.SetAccount(address, recreated);
            batch.SetStorage(address, 3, SlotValue.FromSpanWithoutLeadingZero([0x33]));
        });

        AssertState();
        _persistence.Fold();
        AssertState();

        void AssertState()
        {
            using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(reader.GetAccount(address), Is.EqualTo(recreated));
                Assert.That(GetSlot(reader, address, 1), Is.Null, "pre-destruction slot must not resurrect");
                Assert.That(GetSlot(reader, address, 3), Is.EqualTo([0x33]));
            }
        }
    }

    [Test]
    public void DeleteStorageRange_InBase_ChecksAddressSuffix()
    {
        // Two address hashes sharing the 4-byte storage key prefix: the range delete must remove only the
        // targeted account's rows, re-verifying the 16-byte suffix — in the base tables, not just the overlay.
        byte[] hash1Bytes = new byte[32];
        byte[] hash2Bytes = new byte[32];
        hash1Bytes[0] = hash2Bytes[0] = 0xAA;
        hash1Bytes[1] = hash2Bytes[1] = 0xBB;
        hash1Bytes[2] = hash2Bytes[2] = 0xCC;
        hash1Bytes[3] = hash2Bytes[3] = 0xDD;
        hash1Bytes[4] = 0x11;
        hash2Bytes[4] = 0x22;
        ValueHash256 hash1 = new(hash1Bytes);
        ValueHash256 hash2 = new(hash2Bytes);
        ValueHash256 slotHash = ValueKeccak.Compute([1, 2, 3]);
        byte[] rlpValue = Rlp.Encode(new byte[] { 0x77 }).Bytes;

        Write(batch =>
        {
            batch.SetStorageRawEncoded(hash1, slotHash, rlpValue);
            batch.SetStorageRawEncoded(hash2, slotHash, rlpValue);
        });
        _persistence.Fold();

        Write(batch => batch.DeleteStorageRange(hash1, ValueKeccak.Zero, ValueKeccak.MaxValue));

        AssertState();
        _persistence.Fold();
        AssertState();

        void AssertState()
        {
            using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
            SlotValue value = default;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(reader.TryGetStorageRaw(hash1, slotHash, ref value), Is.False);
                Assert.That(reader.TryGetStorageRaw(hash2, slotHash, ref value), Is.True);
            }
        }
    }

    [Test]
    public void DeleteAccountRange_TombstonesBaseRows()
    {
        // Addresses whose hashes land all over the range; delete a sub-range covering some of them.
        Address[] addresses = [TestItem.AddressA, TestItem.AddressB, TestItem.AddressC, TestItem.AddressD, TestItem.AddressE];
        Write(batch =>
        {
            for (int i = 0; i < addresses.Length; i++) batch.SetAccount(addresses[i], TestItem.GenerateIndexedAccount(i));
        });
        _persistence.Fold();

        ValueHash256[] sortedHashes = addresses.Select(static a => a.ToAccountPath).OrderBy(static h => h, Comparer<ValueHash256>.Default).ToArray();
        // Delete the middle three (by hash order).
        Write(batch => batch.DeleteAccountRange(sortedHashes[1], sortedHashes[3]));

        AssertState();
        _persistence.Fold();
        AssertState();

        void AssertState()
        {
            using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
            using (Assert.EnterMultipleScope())
            {
                foreach (Address address in addresses)
                {
                    ValueHash256 hash = address.ToAccountPath;
                    bool deleted = hash.CompareTo(sortedHashes[1]) >= 0 && hash.CompareTo(sortedHashes[3]) <= 0;
                    Assert.That(reader.GetAccount(address), deleted ? Is.Null : Is.Not.Null, address.ToString());
                }
            }
        }
    }

    [Test]
    public void WriteBatch_OnWrongFromState_Throws()
    {
        StateId state1 = new(1, TestItem.KeccakA);
        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.PreGenesis, state1))
        {
            batch.SetAccount(TestItem.AddressA, TestItem.GenerateIndexedAccount(0));
        }

        StateId wrongFrom = new(5, TestItem.KeccakB);
        Assert.That(() => _persistence.CreateWriteBatch(wrongFrom, new StateId(6, TestItem.KeccakC)),
            Throws.InvalidOperationException);

        // The correct from still works.
        _persistence.CreateWriteBatch(state1, new StateId(2, TestItem.KeccakB)).Dispose();
    }

    [Test]
    public void Restart_ReloadsShardTables_AndOverlay()
    {
        Account folded = TestItem.GenerateIndexedAccount(0);
        Account overlayOnly = TestItem.GenerateIndexedAccount(1);
        Write(batch =>
        {
            batch.SetAccount(TestItem.AddressA, folded);
            batch.SetStorage(TestItem.AddressA, 7, SlotValue.FromSpanWithoutLeadingZero([0x77]));
        });
        _persistence.Fold();
        Write(batch => batch.SetAccount(TestItem.AddressB, overlayOnly));

        _persistence.Dispose();
        _persistence = NewPersistence();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ReadAccount(TestItem.AddressA), Is.EqualTo(folded));
            Assert.That(ReadAccount(TestItem.AddressB), Is.EqualTo(overlayOnly));
            Assert.That(ReadSlot(TestItem.AddressA, 7), Is.EqualTo([0x77]));
        }
    }

    [Test]
    public void OrphanShardTableFiles_AreSweptOnStartup_RegisteredOnesSurvive()
    {
        Write(batch => batch.SetAccount(TestItem.AddressA, TestItem.GenerateIndexedAccount(0)));
        _persistence.Fold();
        _persistence.Dispose();

        // Mimic a fold that crashed after fsyncing new tables but before the registry batch committed.
        string orphanA = Path.Combine(_dir.Path, "a0003_000000ff.st");
        string orphanS = Path.Combine(_dir.Path, "s0007_000000fe.st");
        File.WriteAllBytes(orphanA, [1, 2, 3]);
        File.WriteAllBytes(orphanS, [4, 5, 6]);
        int registeredFileCount = Directory.GetFiles(_dir.Path, "*.st").Length - 2;

        _persistence = NewPersistence();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(orphanA), Is.False, "orphan account table must be swept");
            Assert.That(File.Exists(orphanS), Is.False, "orphan storage table must be swept");
            Assert.That(Directory.GetFiles(_dir.Path, "*.st"), Has.Length.EqualTo(registeredFileCount));
            Assert.That(ReadAccount(TestItem.AddressA), Is.EqualTo(TestItem.GenerateIndexedAccount(0)));
        }
    }

    [Test]
    public void RegistryPointingAtMissingFile_FailsLoudly()
    {
        Write(batch => batch.SetAccount(TestItem.AddressA, TestItem.GenerateIndexedAccount(0)));
        _persistence.Fold();
        _persistence.Dispose();

        foreach (string file in Directory.GetFiles(_dir.Path, "a*.st")) File.Delete(file);

        Assert.That(() => NewPersistence(), Throws.TypeOf<InvalidConfigurationException>());
        _persistence = new ArenaBasePersistence(
            new SnapshotableMemColumnsDb<FlatDbColumns>(), _dir.Path, new FlatDbConfig(), LimboLogs.Instance, AccountShards, StorageShards);
    }

    [Test]
    public void RocksWrittenDb_OpenedAsArena_FailsLoudly()
    {
        SnapshotableMemColumnsDb<FlatDbColumns> rocksDb = new();
        RocksDbPersistence rocks = new(rocksDb, LimboLogs.Instance);
        using (IPersistence.IWriteBatch batch = rocks.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            batch.SetAccount(TestItem.AddressA, TestItem.GenerateIndexedAccount(0));
        }

        using TempPath dir = TempPath.GetTempDirectory();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                () => new ArenaBasePersistence(rocksDb, dir.Path, new FlatDbConfig(), LimboLogs.Instance, AccountShards, StorageShards),
                Throws.TypeOf<InvalidConfigurationException>());
            Assert.That(() => ArenaBasePersistence.ValidateBaseStoreKind(rocksDb, FlatBaseStore.Rocks), Throws.Nothing);
        }
        rocksDb.Dispose();
    }

    [Test]
    public void ArenaWrittenDb_OpenedAsRocks_FailsLoudly()
    {
        Write(batch => batch.SetAccount(TestItem.AddressA, TestItem.GenerateIndexedAccount(0)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => ArenaBasePersistence.ValidateBaseStoreKind(_db, FlatBaseStore.Rocks),
                Throws.TypeOf<InvalidConfigurationException>());
            Assert.That(() => ArenaBasePersistence.ValidateBaseStoreKind(_db, FlatBaseStore.Arena), Throws.Nothing);
        }
    }

    [Test]
    public void FreshDb_AcceptsEitherBackend()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => ArenaBasePersistence.ValidateBaseStoreKind(new SnapshotableMemColumnsDb<FlatDbColumns>(), FlatBaseStore.Rocks), Throws.Nothing);
            Assert.That(() => ArenaBasePersistence.ValidateBaseStoreKind(new SnapshotableMemColumnsDb<FlatDbColumns>(), FlatBaseStore.Arena), Throws.Nothing);
        }
    }

    [Test]
    public void FoldThreshold_TriggersFoldOnBatchDispose()
    {
        _persistence.Dispose();
        _persistence = NewPersistence(foldThresholdBytes: 1);

        Write(batch => batch.SetAccount(TestItem.AddressA, TestItem.GenerateIndexedAccount(0)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(OverlayEntryCount(FlatDbColumns.Account), Is.Zero, "the batch dispose should have folded the overlay");
            Assert.That(ReadAccount(TestItem.AddressA), Is.EqualTo(TestItem.GenerateIndexedAccount(0)));
        }
    }

    [Test]
    public void ReaderCreatedBeforeFold_KeepsItsSnapshot()
    {
        Write(batch => batch.SetAccount(TestItem.AddressA, TestItem.GenerateIndexedAccount(0)));
        using IPersistence.IPersistenceReader before = _persistence.CreateReader();

        _persistence.Fold();
        Write(batch => batch.SetAccount(TestItem.AddressA, TestItem.GenerateIndexedAccount(1)));
        _persistence.Fold();
        using IPersistence.IPersistenceReader after = _persistence.CreateReader();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(before.GetAccount(TestItem.AddressA), Is.EqualTo(TestItem.GenerateIndexedAccount(0)));
            Assert.That(after.GetAccount(TestItem.AddressA), Is.EqualTo(TestItem.GenerateIndexedAccount(1)));
        }
    }

    [Test]
    public void BulkLoad_IsEquivalentToFoldedWrites()
    {
        (Address Address, Account Account)[] accounts = Enumerable.Range(0, 24)
            .Select(static i => (TestItem.Addresses[i], TestItem.GenerateIndexedAccount(i)))
            .ToArray();
        (Address Address, UInt256 Slot, byte[] Value)[] slots = Enumerable.Range(0, 24)
            .SelectMany(static i => new[]
            {
                (TestItem.Addresses[i], (UInt256)1, new byte[] { (byte)(i + 1) }),
                (TestItem.Addresses[i], UInt256.MaxValue, new byte[] { 0x80, (byte)i }),
            })
            .ToArray();

        Write(batch =>
        {
            foreach ((Address address, Account account) in accounts) batch.SetAccount(address, account);
            foreach ((Address address, UInt256 slot, byte[] value) in slots) batch.SetStorage(address, slot, SlotValue.FromSpanWithoutLeadingZero(value));
        });
        _persistence.Fold();

        // Bulk-load the same content, keyed exactly as the hashed layout stores it, into a fresh store.
        List<KeyValuePair<byte[], byte[]>> accountRows = accounts
            .Select(static e => new KeyValuePair<byte[], byte[]>(
                e.Address.ToAccountPath.Bytes[..20].ToArray(),
                AccountDecoder.Slim.EncodeAsBytes(e.Account)))
            .OrderBy(static kv => kv.Key, Bytes.Comparer)
            .ToList();
        List<KeyValuePair<byte[], byte[]>> storageRows = slots
            .Select(static e =>
            {
                ValueHash256 slotHash = ValueKeccak.Zero;
                Span<byte> slotBytes = stackalloc byte[32];
                e.Slot.ToBigEndian(slotBytes);
                slotHash = ValueKeccak.Compute(slotBytes);
                byte[] key = new byte[52];
                BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(key, e.Address.ToAccountPath, slotHash);
                return new KeyValuePair<byte[], byte[]>(key, Rlp.Encode(e.Value.AsSpan().WithoutLeadingZeros()).Bytes);
            })
            .OrderBy(static kv => kv.Key, Bytes.Comparer)
            .ToList();

        using TempPath bulkDir = TempPath.GetTempDirectory();
        using SnapshotableMemColumnsDb<FlatDbColumns> bulkDb = new();
        using ArenaBasePersistence bulkLoaded = new(bulkDb, bulkDir.Path, new FlatDbConfig(), LimboLogs.Instance, AccountShards, StorageShards);
        bulkLoaded.BulkLoad(accountRows, storageRows);

        using IPersistence.IPersistenceReader expected = _persistence.CreateReader();
        using IPersistence.IPersistenceReader actual = bulkLoaded.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            foreach ((Address address, Account account) in accounts)
                Assert.That(actual.GetAccount(address), Is.EqualTo(account), address.ToString());
            foreach ((Address address, UInt256 slot, byte[] value) in slots)
                Assert.That(GetSlot(actual, address, slot), Is.EqualTo(value.AsSpan().WithoutLeadingZeros().ToArray()));
            Assert.That(EnumerateAccounts(actual), Is.EqualTo(EnumerateAccounts(expected)));
        }
    }

    [Test]
    public void RandomOps_MatchOracle_AcrossFolds()
    {
        Random rng = new(0x5eed);
        Address[] addresses = Enumerable.Range(0, 24).Select(_ => TestItem.GetRandomAddress(rng)).Distinct().ToArray();
        UInt256[] slots = [0, 1, 42, 1000, UInt256.MaxValue];

        Dictionary<Address, Account> accountOracle = [];
        Dictionary<(Address, UInt256), byte[]> slotOracle = [];

        for (int round = 0; round < 16; round++)
        {
            // Pre-draw the round's ops so self-destructs can be applied first, mirroring the
            // PersistenceManager contract (per-address SD before SetAccount/SetStorage in a batch);
            // an SD's overlay scan uses the batch-creation snapshot, so it cannot see same-batch writes.
            int ops = rng.Next(1, 12);
            (int Op, Address Address, UInt256 Slot, byte[] Value)[] drawn = new (int, Address, UInt256, byte[])[ops];
            for (int i = 0; i < ops; i++)
            {
                byte[] value = new byte[rng.Next(1, 33)];
                rng.NextBytes(value);
                value[0] = (byte)rng.Next(1, 256); // stripped form: no leading zero
                drawn[i] = (rng.Next(5), addresses[rng.Next(addresses.Length)], slots[rng.Next(slots.Length)], value);
            }

            Write(batch =>
            {
                foreach ((int op, Address address, UInt256 _, byte[] _) in drawn.Where(static d => d.Op == 4))
                {
                    batch.SelfDestruct(address);
                    foreach (UInt256 s in slots) slotOracle.Remove((address, s));
                }

                foreach ((int op, Address address, UInt256 slot, byte[] value) in drawn.Where(static d => d.Op != 4))
                {
                    switch (op)
                    {
                        case 0:
                            Account account = TestItem.GenerateIndexedAccount(rng.Next(1000));
                            batch.SetAccount(address, account);
                            accountOracle[address] = account;
                            break;
                        case 1:
                            batch.SetAccount(address, null);
                            accountOracle.Remove(address);
                            break;
                        case 2:
                            batch.SetStorage(address, slot, SlotValue.FromSpanWithoutLeadingZero(value));
                            slotOracle[(address, slot)] = value;
                            break;
                        case 3:
                            batch.SetStorage(address, slot, null);
                            slotOracle.Remove((address, slot));
                            break;
                    }
                }
            });

            if (rng.Next(3) == 0) _persistence.Fold();

            VerifyAgainstOracle(addresses, slots, accountOracle, slotOracle, rng);
        }

        _persistence.Fold();
        VerifyAgainstOracle(addresses, slots, accountOracle, slotOracle, rng);
        Assert.That(OverlayEntryCount(FlatDbColumns.Account) + OverlayEntryCount(FlatDbColumns.Storage), Is.Zero);
    }

    private void VerifyAgainstOracle(
        Address[] addresses,
        UInt256[] slots,
        Dictionary<Address, Account> accountOracle,
        Dictionary<(Address, UInt256), byte[]> slotOracle,
        Random rng)
    {
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();

        // Point reads.
        foreach (Address address in addresses)
        {
            Assert.That(reader.GetAccount(address), Is.EqualTo(accountOracle.GetValueOrDefault(address)), $"account {address}");
            foreach (UInt256 slot in slots)
            {
                byte[]? expected = slotOracle.GetValueOrDefault((address, slot));
                Assert.That(GetSlot(reader, address, slot), Is.EqualTo(expected), $"slot {slot} of {address}");
            }
        }

        // Full account iteration: hash-ordered keys and decoded values must match the oracle exactly.
        List<(ValueHash256 Key, Account Value)> expectedAccounts = accountOracle
            .Select(static kv =>
            {
                ValueHash256 key = ValueKeccak.Zero;
                kv.Key.ToAccountPath.Bytes[..20].CopyTo(key.BytesAsSpan);
                return (key, kv.Value);
            })
            .OrderBy(static e => e.key, Comparer<ValueHash256>.Default)
            .ToList();
        Assert.That(EnumerateAccounts(reader), Is.EqualTo(expectedAccounts), "full account iteration");

        // Random sub-range iteration exercises seek + bounds across overlay and shard tables.
        byte[] lowBytes = new byte[32];
        byte[] highBytes = new byte[32];
        rng.NextBytes(lowBytes);
        rng.NextBytes(highBytes);
        if (Bytes.BytesComparer.Compare(lowBytes, highBytes) > 0) (lowBytes, highBytes) = (highBytes, lowBytes);
        // The account iterator truncates both bounds to 20 bytes (start inclusive, end exclusive).
        List<(ValueHash256 Key, Account Value)> expectedRange = expectedAccounts
            .Where(e => e.Key.Bytes[..20].SequenceCompareTo(lowBytes.AsSpan(0, 20)) >= 0
                        && e.Key.Bytes[..20].SequenceCompareTo(highBytes.AsSpan(0, 20)) < 0)
            .ToList();
        Assert.That(EnumerateAccounts(reader, new ValueHash256(lowBytes), new ValueHash256(highBytes)), Is.EqualTo(expectedRange), "sub-range account iteration");

        // Per-account storage iteration.
        foreach (Address address in addresses)
        {
            List<(ValueHash256 SlotHash, byte[] Value)> expectedSlots = slots
                .Where(s => slotOracle.ContainsKey((address, s)))
                .Select(s =>
                {
                    Span<byte> slotBytes = stackalloc byte[32];
                    s.ToBigEndian(slotBytes);
                    return (ValueKeccak.Compute(slotBytes), slotOracle[(address, s)]);
                })
                .OrderBy(static e => e.Item1, Comparer<ValueHash256>.Default)
                .ToList();

            List<(ValueHash256, byte[])> actualSlots = [];
            using IPersistence.IFlatIterator iterator = reader.CreateStorageIterator(address.ToAccountPath);
            while (iterator.MoveNext()) actualSlots.Add((iterator.CurrentKey, iterator.CurrentValue.ToArray()));
            Assert.That(actualSlots, Is.EqualTo(expectedSlots), $"storage iteration of {address}");
        }
    }

    private static List<(ValueHash256 Key, Account Value)> EnumerateAccounts(
        IPersistence.IPersistenceReader reader, ValueHash256? start = null, ValueHash256? end = null)
    {
        List<(ValueHash256, Account)> result = [];
        using IPersistence.IFlatIterator iterator = reader.CreateAccountIterator(start ?? ValueKeccak.Zero, end ?? ValueKeccak.MaxValue);
        while (iterator.MoveNext())
        {
            RlpReader ctx = new(iterator.CurrentValue);
            result.Add((iterator.CurrentKey, AccountDecoder.Slim.Decode(ref ctx)!));
        }

        return result;
    }

    [Test]
    public void VerifyTrie_OverFoldedBase_ReportsNoMismatch()
    {
        // The flat-vs-trie verifier co-iterates the merged (overlay ∪ shard table) iterators against the
        // real tries — the persistence-level equivalent of a VerifyWithTrie run over the arena base store.
        using MemDb trieDb = new();
        RawScopedTrieStore trieStore = new(trieDb);
        StateTree stateTree = new(trieStore, LimboLogs.Instance);

        Address address = TestItem.AddressA;
        Address plainAccount = TestItem.AddressB;
        (UInt256 Slot, byte[] Value)[] slots = [((UInt256)1, [0x11]), ((UInt256)2, [0x22, 0x33]), (UInt256.MaxValue, [0x44])];

        Hash256 addressHash = Keccak.Compute(address.Bytes);
        StorageTree storageTree = new((IScopedTrieStore)trieStore.GetStorageTrieNodeResolver(addressHash), LimboLogs.Instance);
        foreach ((UInt256 slot, byte[] value) in slots) storageTree.Set(slot, value);
        storageTree.Commit();

        Account contract = new(1, 100, storageTree.RootHash, Keccak.Compute([1]));
        Account plain = new(2, 200);
        stateTree.Set(address, contract);
        stateTree.Set(plainAccount, plain);
        stateTree.Commit();

        Write(batch =>
        {
            batch.SetAccount(address, contract);
            batch.SetAccount(plainAccount, plain);
            foreach ((UInt256 slot, byte[] value) in slots) batch.SetStorage(address, slot, SlotValue.FromSpanWithoutLeadingZero(value));
        });
        _persistence.Fold();

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        FlatTrieVerifier verifier = new(LimboLogs.Instance);
        verifier.Verify(reader, trieStore, stateTree.RootHash, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verifier.Stats.AccountCount, Is.EqualTo(2));
            Assert.That(verifier.Stats.SlotCount, Is.EqualTo(slots.Length));
            Assert.That(verifier.Stats.MismatchedAccount, Is.Zero);
            Assert.That(verifier.Stats.MismatchedSlot, Is.Zero);
            Assert.That(verifier.Stats.MissingInFlat, Is.Zero);
            Assert.That(verifier.Stats.MissingInTrie, Is.Zero);
        }
    }
}
