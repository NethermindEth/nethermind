// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autofac;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Init.Modules;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.PaprikaFlat;
using Nethermind.State.Flat.Test.Persistence.PaprikaFlat;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;
using Paprika.Store.PageManagers;

namespace Nethermind.State.Flat.Test;

[TestFixtureSource(nameof(TestConfigs))]
public class PersistenceScenario(PersistenceScenario.TestConfiguration configuration)
{
    private const long PaprikaFileSize = 64 * 1024 * 1024;

    private TempPath _tmpDirectory = null!;
    private IContainer _container = null!;
    private IPersistence _persistence = null!;

    // Helper method to convert TryGetSlot to GetSlot-like behavior
    private static byte[]? GetSlot(IPersistence.IPersistenceReader reader, Address address, in UInt256 slot)
    {
        UInt256 slotValue = default;
        if (reader.TryGetSlot(address, in slot, ref slotValue))
        {
            return slotValue.ToMinimalBigEndian();
        }
        return null;
    }

    public record TestConfiguration(FlatDbConfig FlatDbConfig, string Name)
    {
        public override string ToString() => Name;
    }

    public static IEnumerable<TestConfiguration> TestConfigs()
    {
        foreach (FlatLayout layout in Enum.GetValues<FlatLayout>())
        {
            // Resolves to Flat or FlatInTrie by the machine's memory.
            if (layout == FlatLayout.Auto) continue;

            // PaprikaFlat refuses to start without the trie node log.
            if (layout != FlatLayout.PaprikaFlat)
            {
                yield return new TestConfiguration(new FlatDbConfig()
                {
                    Enabled = true,
                    Layout = layout
                }, layout.ToString());
            }

            // A generation small enough that the scenarios roll and merge generations while readers are open.
            yield return new TestConfiguration(new FlatDbConfig()
            {
                Enabled = true,
                Layout = layout,
                TrieNodeLogEnabled = true,
                TrieNodeLogStateBytes = 8192,
                TrieNodeLogStorageBytes = 8192,
                TrieNodeLogSecondLevelMergeLag = -1,
                PaprikaFlatFileSizeBytes = PaprikaFileSize,
            }, $"{layout}+TrieNodeLog");

            if (layout == FlatLayout.PaprikaFlat)
            {
                // Below the minimum of 8 windows, so the scenarios run while the mapping is released all along.
                yield return new TestConfiguration(new FlatDbConfig()
                {
                    Enabled = true,
                    Layout = layout,
                    TrieNodeLogEnabled = true,
                    TrieNodeLogStateBytes = 8192,
                    TrieNodeLogStorageBytes = 8192,
                    TrieNodeLogSecondLevelMergeLag = -1,
                    PaprikaFlatFileSizeBytes = PaprikaFileSize,
                    PaprikaFlatPageCacheBytes = 256 * 1024,
                }, $"{layout}+TrieNodeLog+PageCache256KiB");
            }

            yield return new TestConfiguration(new FlatDbConfig()
            {
                Enabled = true,
                Layout = layout,
                TrieNodeLogEnabled = true,
                TrieNodeLogStateBytes = 8192,
                TrieNodeLogStorageBytes = 8192,
                PaprikaFlatFileSizeBytes = PaprikaFileSize,
            }, $"{layout}+TrieNodeLogSecondLevel");
        }
    }

    private static bool IsPreimage(FlatLayout layout) => layout is FlatLayout.PreimageFlatV1 or FlatLayout.PreimageFlat;

    // Range deletes and self-destructs scan the RocksDB snapshot, so nodes still held only by the trie node log
    // are not found; sync batches, which issue the range deletes, bypass the log for this reason.
    private void IgnoreScanDeletesOverTrieNodeLog()
    {
        if (configuration.FlatDbConfig.TrieNodeLogEnabled)
            Assert.Ignore("Scan-based trie node deletes do not see nodes held by the trie node log");
    }

    [SetUp]
    public void Setup()
    {
        _tmpDirectory = TempPath.GetTempDirectory();
        Open();
    }

    /// <summary>Builds the node's container over <see cref="_tmpDirectory"/>, as a start of the node does.</summary>
    private void Open()
    {
        _container = new ContainerBuilder()
            .AddModule(
                new NethermindModule(
                    new ChainSpec(),
                    new ConfigProvider(
                        configuration.FlatDbConfig,
                        new InitConfig()
                        {
                            BaseDbPath = _tmpDirectory.Path,
                        }),
                    LimboLogs.Instance))
            .AddSingleton<IProcessExitSource>(Substitute.For<IProcessExitSource>())
            .Build();

        _persistence = _container.Resolve<IPersistence>();
    }

    [TearDown]
    public void TearDown()
    {
        _container.Dispose();
        _tmpDirectory.Dispose();
    }

    [Test]
    public void TestCanWriteAccount()
    {
        Account acc = TestItem.GenerateIndexedAccount(0);
        Address address = TestItem.AddressA;

        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        {
            Assert.That(reader.GetAccount(address), Is.Null);
        }

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, acc);
        }

        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        {
            Assert.That(reader.GetAccount(address), Is.EqualTo(acc));
        }
    }

    [Test]
    public void TestCanAccountSnapshot()
    {
        Address address = TestItem.AddressA;

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, TestItem.GenerateIndexedAccount(0));
        }

        using IPersistence.IPersistenceReader reader1 = _persistence.CreateReader();

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, TestItem.GenerateIndexedAccount(1));
        }

        using IPersistence.IPersistenceReader reader2 = _persistence.CreateReader();

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, TestItem.GenerateIndexedAccount(2));
        }

        using IPersistence.IPersistenceReader reader3 = _persistence.CreateReader();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader1.GetAccount(address), Is.EqualTo(TestItem.GenerateIndexedAccount(0)));
            Assert.That(reader2.GetAccount(address), Is.EqualTo(TestItem.GenerateIndexedAccount(1)));
            Assert.That(reader3.GetAccount(address), Is.EqualTo(TestItem.GenerateIndexedAccount(2)));
        }
    }

    [Test]
    public void TestSelfDestructAccount()
    {
        Account acc = TestItem.GenerateIndexedAccount(0);
        Account acc2 = TestItem.GenerateIndexedAccount(1);
        Address address = TestItem.AddressA;
        Address address2 = TestItem.AddressB;

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, acc);
            writer.SetStorage(address, UInt256.MinValue, BaseFlatPersistence.DecodeSlotValue([1]));
            writer.SetStorage(address, 123, BaseFlatPersistence.DecodeSlotValue([2]));
            writer.SetStorage(address, UInt256.MaxValue, BaseFlatPersistence.DecodeSlotValue([3]));

            writer.SetAccount(address2, acc2);
            writer.SetStorage(address2, UInt256.MinValue, BaseFlatPersistence.DecodeSlotValue([1]));
            writer.SetStorage(address2, 123, BaseFlatPersistence.DecodeSlotValue([2]));
            writer.SetStorage(address2, UInt256.MaxValue, BaseFlatPersistence.DecodeSlotValue([3]));
        }

        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetSlot(reader, address, UInt256.MinValue), Is.EqualTo([1]));
            Assert.That(GetSlot(reader, address, 123), Is.EqualTo([2]));
            Assert.That(GetSlot(reader, address, UInt256.MaxValue), Is.EqualTo([3]));
        }

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SelfDestruct(address);
        }

        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetSlot(reader, address, UInt256.MinValue), Is.Null);
            Assert.That(GetSlot(reader, address, 123), Is.Null);
            Assert.That(GetSlot(reader, address, UInt256.MaxValue), Is.Null);

            Assert.That(GetSlot(reader, address2, UInt256.MinValue), Is.EqualTo([1]));
            Assert.That(GetSlot(reader, address2, 123), Is.EqualTo([2]));
            Assert.That(GetSlot(reader, address2, UInt256.MaxValue), Is.EqualTo([3]));
        }
    }

    [Test]
    public void TestCanWriteAndReadStorage()
    {
        Account acc = TestItem.GenerateIndexedAccount(0);
        Address address = TestItem.AddressA;

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, acc);
        }

        // Initially, slots should be null
        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetSlot(reader, address, UInt256.MinValue), Is.Null);
            Assert.That(GetSlot(reader, address, UInt256.MaxValue), Is.Null);
        }

        // Write various storage slots
        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStorage(address, UInt256.MinValue, BaseFlatPersistence.DecodeSlotValue([1, 2, 3]));
            writer.SetStorage(address, 42, BaseFlatPersistence.DecodeSlotValue([0x42]));
            writer.SetStorage(address, 12345, BaseFlatPersistence.DecodeSlotValue([0x10, 0x20, 0x30, 0x40]));
            writer.SetStorage(address, UInt256.MaxValue, BaseFlatPersistence.DecodeSlotValue([0xff, 0xfe, 0xfd]));
        }

        // Verify all slots can be read back
        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetSlot(reader, address, UInt256.MinValue), Is.EqualTo([1, 2, 3]));
            Assert.That(GetSlot(reader, address, 42), Is.EqualTo([0x42]));
            Assert.That(GetSlot(reader, address, 12345), Is.EqualTo([0x10, 0x20, 0x30, 0x40]));
            Assert.That(GetSlot(reader, address, UInt256.MaxValue), Is.EqualTo([0xff, 0xfe, 0xfd]));
        }
    }

    [Test]
    public void TestCanStorageSnapshot()
    {
        Account acc = TestItem.GenerateIndexedAccount(0);
        Address address = TestItem.AddressA;
        UInt256 slot = 100;

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, acc);
            writer.SetStorage(address, slot, BaseFlatPersistence.DecodeSlotValue([1]));
        }

        using IPersistence.IPersistenceReader reader1 = _persistence.CreateReader();

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStorage(address, slot, BaseFlatPersistence.DecodeSlotValue([2]));
        }

        using IPersistence.IPersistenceReader reader2 = _persistence.CreateReader();

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStorage(address, slot, BaseFlatPersistence.DecodeSlotValue([3]));
        }

        using IPersistence.IPersistenceReader reader3 = _persistence.CreateReader();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetSlot(reader1, address, slot), Is.EqualTo([1]));
            Assert.That(GetSlot(reader2, address, slot), Is.EqualTo([2]));
            Assert.That(GetSlot(reader3, address, slot), Is.EqualTo([3]));
        }
    }

    [Test]
    public void TestRemoveAccount()
    {
        Account acc = TestItem.GenerateIndexedAccount(0);
        Address address = TestItem.AddressA;

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, acc);
            writer.SetStorage(address, 1, BaseFlatPersistence.DecodeSlotValue([0x01]));
            writer.SetStorage(address, 2, BaseFlatPersistence.DecodeSlotValue([0x02]));
        }

        // Verify account and storage exist
        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(reader.GetAccount(address), Is.EqualTo(acc));
                Assert.That(GetSlot(reader, address, 1), Is.EqualTo([0x01]));
            }
        }

        // Remove account
        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, null);
        }

        // Verify account is removed (storage should remain unless explicitly removed)
        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        {
            Assert.That(reader.GetAccount(address), Is.Null);
        }
    }

    [Test]
    public void TestRawOperations()
    {
        if (IsPreimage(configuration.FlatDbConfig.Layout)) Assert.Ignore("Preimage mode does not support raw operation");

        Account acc = TestItem.GenerateIndexedAccount(0);
        Hash256 addrHash = new(TestItem.AddressA.ToAccountPath.Bytes);
        Hash256 slotHash = Keccak.Compute([1, 2, 3]);

        // Test raw account operations
        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccountRaw(addrHash, acc);
        }

        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        {
            byte[]? rawAccount = reader.GetAccountRaw(addrHash);
            Assert.That(rawAccount, Is.Not.Null);

            // Decode and verify
            RlpReader ctx = new(rawAccount);
            Assert.That(AccountDecoder.Slim.Decode(ref ctx), Is.EqualTo(acc));
        }

        // Test raw storage operations
        byte[] storageValue = Bytes.FromHexString("0x000000ffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            ReadOnlySpan<byte> stripped = ((ReadOnlySpan<byte>)storageValue).WithoutLeadingZeros();
            writer.SetStorageRawEncoded(addrHash, slotHash, Rlp.Encode(stripped).Bytes);
        }

        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        {
            UInt256 rawValue = default;
            Assert.That(reader.TryGetStorageRaw(addrHash, slotHash, ref rawValue), Is.EqualTo(storageValue is not null));
            if (storageValue is not null)
            {
                Assert.That(rawValue.ToMinimalBigEndian(), Is.EqualTo(storageValue.WithoutLeadingZeros().ToArray()));
            }
        }
    }

    [Test]
    public void TestConcurrentSnapshots()
    {
        Account acc = TestItem.GenerateIndexedAccount(0);
        Address address = TestItem.AddressA;
        UInt256 slot1 = 100;
        UInt256 slot2 = 200;

        // Initial state
        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, acc);
            writer.SetStorage(address, slot1, BaseFlatPersistence.DecodeSlotValue([1]));
            writer.SetStorage(address, slot2, BaseFlatPersistence.DecodeSlotValue([10]));
        }

        using IPersistence.IPersistenceReader reader1 = _persistence.CreateReader();

        // Modify account and slot1
        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, TestItem.GenerateIndexedAccount(1));
            writer.SetStorage(address, slot1, BaseFlatPersistence.DecodeSlotValue([2]));
        }

        using IPersistence.IPersistenceReader reader2 = _persistence.CreateReader();

        // Modify slot2
        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStorage(address, slot2, BaseFlatPersistence.DecodeSlotValue([20]));
        }

        using IPersistence.IPersistenceReader reader3 = _persistence.CreateReader();

        using (Assert.EnterMultipleScope())
        {
            // Verify reader1 sees initial state
            Assert.That(reader1.GetAccount(address), Is.EqualTo(acc));
            Assert.That(GetSlot(reader1, address, slot1), Is.EqualTo([1]));
            Assert.That(GetSlot(reader1, address, slot2), Is.EqualTo([10]));

            // Verify reader2 sees second state
            Assert.That(reader2.GetAccount(address), Is.EqualTo(TestItem.GenerateIndexedAccount(1)));
            Assert.That(GetSlot(reader2, address, slot1), Is.EqualTo([2]));
            Assert.That(GetSlot(reader2, address, slot2), Is.EqualTo([10]));

            // Verify reader3 sees final state
            Assert.That(reader3.GetAccount(address), Is.EqualTo(TestItem.GenerateIndexedAccount(1)));
            Assert.That(GetSlot(reader3, address, slot1), Is.EqualTo([2]));
            Assert.That(GetSlot(reader3, address, slot2), Is.EqualTo([20]));
        }
    }

    [Test]
    public void TestStorageAcrossMultipleAccounts()
    {
        Address addr1 = TestItem.AddressA;
        Address addr2 = TestItem.AddressB;
        Address addr3 = TestItem.AddressC;
        UInt256 slot = 42;

        // Write same slot number for different accounts
        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(addr1, TestItem.GenerateIndexedAccount(0));
            writer.SetAccount(addr2, TestItem.GenerateIndexedAccount(1));
            writer.SetAccount(addr3, TestItem.GenerateIndexedAccount(2));

            writer.SetStorage(addr1, slot, BaseFlatPersistence.DecodeSlotValue([0x11]));
            writer.SetStorage(addr2, slot, BaseFlatPersistence.DecodeSlotValue([0x22]));
            writer.SetStorage(addr3, slot, BaseFlatPersistence.DecodeSlotValue([0x33]));
        }

        // Verify each account has its own isolated storage
        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetSlot(reader, addr1, slot), Is.EqualTo([0x11]));
            Assert.That(GetSlot(reader, addr2, slot), Is.EqualTo([0x22]));
            Assert.That(GetSlot(reader, addr3, slot), Is.EqualTo([0x33]));
        }

        // Modify storage for addr2 only
        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStorage(addr2, slot, BaseFlatPersistence.DecodeSlotValue([0xff]));
        }

        // Verify only addr2's storage changed
        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetSlot(reader, addr1, slot), Is.EqualTo([0x11]));
            Assert.That(GetSlot(reader, addr2, slot), Is.EqualTo([0xff]));
            Assert.That(GetSlot(reader, addr3, slot), Is.EqualTo([0x33]));
        }
    }

    [Test]
    public void TestCanWriteAndReadTrieNodes()
    {
        // State trie nodes with various path lengths
        TreePath stateShortPath = TreePath.FromHexString("12345"); // <=5 nibbles -> stateTopNodes
        TreePath stateMediumPath = TreePath.FromHexString("123456789abc"); // >5 nibbles -> stateNodes
        TreePath stateLongPath = TreePath.FromHexString("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

        byte[] stateShortRlp = [0xc1, 0x01];
        byte[] stateMediumRlp = [0xc1, 0x02];
        byte[] stateLongRlp = [0xc1, 0x03];

        // Storage trie nodes for different accounts
        Hash256 account1 = TestItem.KeccakA;
        Hash256 account2 = TestItem.KeccakB;
        TreePath storageShortPath = TreePath.FromHexString("abcd");
        TreePath storageLongPath = TreePath.FromHexString("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789");

        byte[] storage1ShortRlp = [0xc1, 0xaa];
        byte[] storage1LongRlp = [0xc1, 0xab];
        byte[] storage2ShortRlp = [0xc1, 0xbb];

        // Write all trie nodes
        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            // State trie nodes (address=null)
            writer.SetStateTrieNode(in stateShortPath, stateShortRlp);
            writer.SetStateTrieNode(in stateMediumPath, stateMediumRlp);
            writer.SetStateTrieNode(in stateLongPath, stateLongRlp);

            // Storage trie nodes (with account address)
            writer.SetStorageTrieNode(account1, in storageShortPath, storage1ShortRlp);
            writer.SetStorageTrieNode(account1, in storageLongPath, storage1LongRlp);
            writer.SetStorageTrieNode(account2, in storageShortPath, storage2ShortRlp);
        }

        // Verify all nodes
        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        using (Assert.EnterMultipleScope())
        {
            // State trie nodes
            Assert.That(reader.TryLoadStateRlp(in stateShortPath, ReadFlags.None), Is.EqualTo(stateShortRlp));
            Assert.That(reader.TryLoadStateRlp(in stateMediumPath, ReadFlags.None), Is.EqualTo(stateMediumRlp));
            Assert.That(reader.TryLoadStateRlp(in stateLongPath, ReadFlags.None), Is.EqualTo(stateLongRlp));

            // Storage trie nodes - verify account isolation
            Assert.That(reader.TryLoadStorageRlp(account1, in storageShortPath, ReadFlags.None), Is.EqualTo(storage1ShortRlp));
            Assert.That(reader.TryLoadStorageRlp(account1, in storageLongPath, ReadFlags.None), Is.EqualTo(storage1LongRlp));
            Assert.That(reader.TryLoadStorageRlp(account2, in storageShortPath, ReadFlags.None), Is.EqualTo(storage2ShortRlp));

            // State and storage at same path are separate
            Assert.That(reader.TryLoadStateRlp(in storageShortPath, ReadFlags.None), Is.Null);
        }
    }

    [Test]
    public void TestTrieNodeSnapshot()
    {
        TreePath path = TreePath.FromHexString("abcdef");

        byte[] rlpData1 = [0xc1, 0x01];
        byte[] rlpData2 = [0xc1, 0x02];
        byte[] rlpData3 = [0xc1, 0x03];

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStateTrieNode(in path, rlpData1);
        }
        using IPersistence.IPersistenceReader reader1 = _persistence.CreateReader();

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStateTrieNode(in path, rlpData2);
        }
        using IPersistence.IPersistenceReader reader2 = _persistence.CreateReader();

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStateTrieNode(in path, rlpData3);
        }
        using IPersistence.IPersistenceReader reader3 = _persistence.CreateReader();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader1.TryLoadStateRlp(in path, ReadFlags.None), Is.EqualTo(rlpData1));
            Assert.That(reader2.TryLoadStateRlp(in path, ReadFlags.None), Is.EqualTo(rlpData2));
            Assert.That(reader3.TryLoadStateRlp(in path, ReadFlags.None), Is.EqualTo(rlpData3));
        }
    }

    [Test]
    public void TestTrieNodeBoundaryPathLengths()
    {
        // Test boundary conditions for path length thresholds:
        // StateNodesTop: 0-5, StateNodes: 6-15, FallbackNodes: 16+
        // StorageNodes: 0-15, FallbackNodes: 16+

        // State trie boundary paths
        TreePath statePath5 = TreePath.FromHexString("12345"); // exactly 5 -> StateNodesTop
        TreePath statePath6 = TreePath.FromHexString("123456"); // exactly 6 -> StateNodes
        TreePath statePath15 = TreePath.FromHexString("123456789abcdef"); // exactly 15 -> StateNodes
        TreePath statePath16 = TreePath.FromHexString("123456789abcdef0"); // exactly 16 -> FallbackNodes

        // Storage trie boundary paths
        Hash256 account = TestItem.KeccakA;
        TreePath storagePath15 = TreePath.FromHexString("abcdef123456789"); // exactly 15 -> StorageNodes
        TreePath storagePath16 = TreePath.FromHexString("abcdef1234567890"); // exactly 16 -> FallbackNodes

        byte[] rlp5 = [0xc1, 0x05];
        byte[] rlp6 = [0xc1, 0x06];
        byte[] rlp15 = [0xc1, 0x0f];
        byte[] rlp16 = [0xc1, 0x10];
        byte[] storageRlp15 = [0xc1, 0x1f];
        byte[] storageRlp16 = [0xc1, 0x20];

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStateTrieNode(in statePath5, rlp5);
            writer.SetStateTrieNode(in statePath6, rlp6);
            writer.SetStateTrieNode(in statePath15, rlp15);
            writer.SetStateTrieNode(in statePath16, rlp16);
            writer.SetStorageTrieNode(account, in storagePath15, storageRlp15);
            writer.SetStorageTrieNode(account, in storagePath16, storageRlp16);
        }

        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.TryLoadStateRlp(in statePath5, ReadFlags.None), Is.EqualTo(rlp5));
            Assert.That(reader.TryLoadStateRlp(in statePath6, ReadFlags.None), Is.EqualTo(rlp6));
            Assert.That(reader.TryLoadStateRlp(in statePath15, ReadFlags.None), Is.EqualTo(rlp15));
            Assert.That(reader.TryLoadStateRlp(in statePath16, ReadFlags.None), Is.EqualTo(rlp16));
            Assert.That(reader.TryLoadStorageRlp(account, in storagePath15, ReadFlags.None), Is.EqualTo(storageRlp15));
            Assert.That(reader.TryLoadStorageRlp(account, in storagePath16, ReadFlags.None), Is.EqualTo(storageRlp16));
        }
    }

    [Test]
    public void TestSelfDestructTrieNodes()
    {
        IgnoreScanDeletesOverTrieNodeLog();
        // Test that SelfDestruct removes storage trie nodes for an account
        // This tests both shortened storage nodes (path ≤15) and fallback storage nodes (path >15)

        // SelfDestruct takes Address, but SetTrieNodes/TryLoadRlp take Hash256 (keccak of address)
        Address address1 = TestItem.AddressA;
        Address address2 = TestItem.AddressB;
        Hash256 account1Hash = Keccak.Compute(address1.Bytes);
        Hash256 account2Hash = Keccak.Compute(address2.Bytes);

        // Various path lengths to test both StorageNodes and FallbackNodes columns
        TreePath shortPath = TreePath.FromHexString("abcd"); // 4 nibbles -> StorageNodes
        TreePath mediumPath = TreePath.FromHexString("123456789abcdef"); // 15 nibbles -> StorageNodes
        TreePath longPath = TreePath.FromHexString("0123456789abcdef0123456789abcdef01234567"); // 40 nibbles -> FallbackNodes

        byte[] rlpShort = [0xc1, 0x01];
        byte[] rlpMedium = [0xc1, 0x02];
        byte[] rlpLong = [0xc1, 0x03];

        // Write trie nodes for both accounts
        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            // Account 1 storage trie nodes
            writer.SetStorageTrieNode(account1Hash, in shortPath, rlpShort);
            writer.SetStorageTrieNode(account1Hash, in mediumPath, rlpMedium);
            writer.SetStorageTrieNode(account1Hash, in longPath, rlpLong);

            // Account 2 storage trie nodes (same paths, different account)
            writer.SetStorageTrieNode(account2Hash, in shortPath, rlpShort);
            writer.SetStorageTrieNode(account2Hash, in mediumPath, rlpMedium);
            writer.SetStorageTrieNode(account2Hash, in longPath, rlpLong);
        }

        // Verify all nodes exist
        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.TryLoadStorageRlp(account1Hash, in shortPath, ReadFlags.None), Is.EqualTo(rlpShort));
            Assert.That(reader.TryLoadStorageRlp(account1Hash, in mediumPath, ReadFlags.None), Is.EqualTo(rlpMedium));
            Assert.That(reader.TryLoadStorageRlp(account1Hash, in longPath, ReadFlags.None), Is.EqualTo(rlpLong));
            Assert.That(reader.TryLoadStorageRlp(account2Hash, in shortPath, ReadFlags.None), Is.EqualTo(rlpShort));
            Assert.That(reader.TryLoadStorageRlp(account2Hash, in mediumPath, ReadFlags.None), Is.EqualTo(rlpMedium));
            Assert.That(reader.TryLoadStorageRlp(account2Hash, in longPath, ReadFlags.None), Is.EqualTo(rlpLong));
        }

        // SelfDestruct account1 (uses Address, internally converts to hash)
        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SelfDestruct(address1);
        }

        // Verify account1's trie nodes are deleted, account2's remain
        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        using (Assert.EnterMultipleScope())
        {
            // Account 1 nodes should be gone
            Assert.That(reader.TryLoadStorageRlp(account1Hash, in shortPath, ReadFlags.None), Is.Null);
            Assert.That(reader.TryLoadStorageRlp(account1Hash, in mediumPath, ReadFlags.None), Is.Null);
            Assert.That(reader.TryLoadStorageRlp(account1Hash, in longPath, ReadFlags.None), Is.Null);

            // Account 2 nodes should still exist
            Assert.That(reader.TryLoadStorageRlp(account2Hash, in shortPath, ReadFlags.None), Is.EqualTo(rlpShort));
            Assert.That(reader.TryLoadStorageRlp(account2Hash, in mediumPath, ReadFlags.None), Is.EqualTo(rlpMedium));
            Assert.That(reader.TryLoadStorageRlp(account2Hash, in longPath, ReadFlags.None), Is.EqualTo(rlpLong));
        }
    }

    [Test]
    public void TestSelfDestructTrieNodesWithSimilarAddressHashPrefix()
    {
        IgnoreScanDeletesOverTrieNodeLog();
        // Test that SelfDestruct correctly differentiates accounts even when their hashes
        // might share the first 4 bytes (the prefix used in storage key encoding).
        // The storage key uses first 4 bytes of hash as prefix, remaining 16 bytes at end.
        // This tests that the suffix comparison works correctly.

        // Create two hashes that share the same first 4 bytes but differ in later bytes
        // We bypass Address->Hash256 conversion to directly test the hash-based logic
        byte[] hash1Bytes = new byte[32];
        byte[] hash2Bytes = new byte[32];
        // Same prefix (first 4 bytes)
        hash1Bytes[0] = 0xAA; hash1Bytes[1] = 0xBB; hash1Bytes[2] = 0xCC; hash1Bytes[3] = 0xDD;
        hash2Bytes[0] = 0xAA; hash2Bytes[1] = 0xBB; hash2Bytes[2] = 0xCC; hash2Bytes[3] = 0xDD;
        // Different suffix (bytes 4-19 are used in the key suffix check)
        hash1Bytes[4] = 0x11;
        hash2Bytes[4] = 0x22;

        Hash256 account1Hash = new(hash1Bytes);
        Hash256 account2Hash = new(hash2Bytes);

        TreePath shortPath = TreePath.FromHexString("1234"); // -> StorageNodes
        TreePath longPath = TreePath.FromHexString("0123456789abcdef0123456789abcdef01234567"); // -> FallbackNodes

        byte[] rlp1 = [0xc1, 0x11];
        byte[] rlp2 = [0xc1, 0x22];

        // Write trie nodes using the hashes directly
        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStorageTrieNode(account1Hash, in shortPath, rlp1);
            writer.SetStorageTrieNode(account1Hash, in longPath, rlp1);
            writer.SetStorageTrieNode(account2Hash, in shortPath, rlp2);
            writer.SetStorageTrieNode(account2Hash, in longPath, rlp2);
        }

        // Verify all nodes exist before SelfDestruct
        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.TryLoadStorageRlp(account1Hash, in shortPath, ReadFlags.None), Is.EqualTo(rlp1));
            Assert.That(reader.TryLoadStorageRlp(account1Hash, in longPath, ReadFlags.None), Is.EqualTo(rlp1));
            Assert.That(reader.TryLoadStorageRlp(account2Hash, in shortPath, ReadFlags.None), Is.EqualTo(rlp2));
            Assert.That(reader.TryLoadStorageRlp(account2Hash, in longPath, ReadFlags.None), Is.EqualTo(rlp2));
        }

        // SelfDestruct account1 using an address that hashes to account1Hash
        // Note: We use AddressC since we need a real Address for SelfDestruct
        // This tests the general SelfDestruct flow; the prefix collision test above
        // verifies the data is correctly written with similar prefixes
        Address address1 = TestItem.AddressC;
        Hash256 address1Hash = Keccak.Compute(address1.Bytes);

        // Write and then delete using the real address flow
        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStorageTrieNode(address1Hash, in shortPath, rlp1);
            writer.SetStorageTrieNode(address1Hash, in longPath, rlp1);
        }

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SelfDestruct(address1);
        }

        // Verify address1's trie nodes are deleted
        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.TryLoadStorageRlp(address1Hash, in shortPath, ReadFlags.None), Is.Null);
            Assert.That(reader.TryLoadStorageRlp(address1Hash, in longPath, ReadFlags.None), Is.Null);

            // The manually created hashes should still exist (they weren't self-destructed)
            Assert.That(reader.TryLoadStorageRlp(account1Hash, in shortPath, ReadFlags.None), Is.EqualTo(rlp1));
            Assert.That(reader.TryLoadStorageRlp(account2Hash, in shortPath, ReadFlags.None), Is.EqualTo(rlp2));
        }
    }

    // Each case: the value range [from, to] (the subtree of some root path) and nodes spread across all columns
    // (top 0-5 / shortened 6-15 / fallback 16+), tagged with whether their whole subtree is contained in the range.
    private static IEnumerable<TestCaseData> SubtreeDeleteCases()
    {
        // Shallow "ab" subtree: everything under ab is fully contained; aa/ac neighbours are not.
        yield return Case("ab", new (string, bool)[]
        {
            ("ab00", true), ("abffff", true), ("ab00000000", true), ("abffffffffffff", true),
            ("ab".PadRight(32, '0'), true), ("ab".PadRight(32, 'f'), true),
            ("ab".PadRight(64, '0'), true), ("ab".PadRight(64, 'f'), true),
            ("aa00", false), ("ac00", false), ("aa".PadRight(64, '0'), false), ("ac".PadRight(64, '0'), false),
        });

        // Deep subtree (depth 20): ancestors on the zero tail (depths 2/10/16/18) only partially overlap the range
        // (their subtree overflows `to`), so they are preserved; the root (depth 20) and its descendants are removed.
        yield return Case("ab".PadRight(20, '0'), new (string, bool)[]
        {
            ("ab", false), ("ab".PadRight(10, '0'), false), ("ab".PadRight(16, '0'), false), ("ab".PadRight(18, '0'), false),
            ("ab".PadRight(20, '0'), true), ("ab".PadRight(20, '0') + "cccc", true), ("ab".PadRight(32, '0'), true), ("ab".PadRight(64, '0'), true),
            ("aa".PadRight(20, '0'), false), ("ac".PadRight(20, '0'), false), ("aa".PadRight(64, '0'), false), ("ac".PadRight(64, '0'), false),
        });

        // Shortened-depth subtree (depth 10): top ancestor (depth 2) and shortened ancestors (depths 6/8) preserved;
        // root (depth 10) and descendants removed.
        yield return Case("ab".PadRight(10, '0'), new (string, bool)[]
        {
            ("ab", false), ("ab".PadRight(6, '0'), false), ("ab".PadRight(8, '0'), false),
            ("ab".PadRight(10, '0'), true), ("ab".PadRight(12, '0'), true), ("ab".PadRight(32, '0'), true), ("ab".PadRight(64, '0'), true),
            ("aa".PadRight(64, '0'), false), ("ac".PadRight(64, '0'), false),
        });

        // Whole trie: [Zero, MaxValue] contains every node.
        yield return new TestCaseData(ValueKeccak.Zero, ValueKeccak.MaxValue, new (string, bool)[]
        {
            ("ab", true), ("ab".PadRight(10, '0'), true), ("ab".PadRight(64, '0'), true), ("cd".PadRight(64, 'f'), true),
        }).SetName("Whole trie");

        static TestCaseData Case(string rootHex, (string, bool)[] nodes)
        {
            TreePath root = TreePath.FromHexString(rootHex);
            return new TestCaseData(root.ToLowerBoundPath(), root.ToUpperBoundPath(), nodes).SetName($"Subtree depth {rootHex.Length}");
        }
    }

    [TestCaseSource(nameof(SubtreeDeleteCases))]
    public void TestDeleteStateTrieNodeRange(ValueHash256 from, ValueHash256 to, (string Path, bool Deleted)[] nodes)
    {
        IgnoreScanDeletesOverTrieNodeLog();
        byte[] rlp = [0xc1, 0x11];

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis))
            foreach ((string p, _) in nodes) writer.SetStateTrieNode(TreePath.FromHexString(p), rlp);

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis))
            writer.DeleteStateTrieNodeRange(from, to);

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            foreach ((string p, bool del) in nodes)
            {
                byte[]? node = reader.TryLoadStateRlp(TreePath.FromHexString(p), ReadFlags.None);
                Assert.That(node, del ? Is.Null : Is.EqualTo(rlp), p);
            }
        }
    }

    [TestCaseSource(nameof(SubtreeDeleteCases))]
    public void TestDeleteStorageTrieNodeRange(ValueHash256 from, ValueHash256 to, (string Path, bool Deleted)[] nodes)
    {
        IgnoreScanDeletesOverTrieNodeLog();
        Hash256 account = TestItem.KeccakA;
        byte[] rlp = [0xc1, 0x11];

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis))
            foreach ((string p, _) in nodes) writer.SetStorageTrieNode(account, TreePath.FromHexString(p), rlp);

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis))
            writer.DeleteStorageTrieNodeRange(new ValueHash256(account.Bytes), from, to);

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            foreach ((string p, bool del) in nodes)
            {
                byte[]? node = reader.TryLoadStorageRlp(account, TreePath.FromHexString(p), ReadFlags.None);
                Assert.That(node, del ? Is.Null : Is.EqualTo(rlp), p);
            }
        }
    }

    [Test]
    public void TestAccountIterator_EnumeratesAllAccounts()
    {
        // Write multiple accounts
        Address addr1 = TestItem.AddressA;
        Address addr2 = TestItem.AddressB;
        Address addr3 = TestItem.AddressC;

        Account acc1 = TestItem.GenerateIndexedAccount(1);
        Account acc2 = TestItem.GenerateIndexedAccount(2);
        Account acc3 = TestItem.GenerateIndexedAccount(3);

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(addr1, acc1);
            writer.SetAccount(addr2, acc2);
            writer.SetAccount(addr3, acc3);
        }

        // Use iterator to enumerate accounts
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        using IPersistence.IFlatIterator iterator = reader.CreateAccountIterator();

        int count = 0;
        while (iterator.MoveNext())
        {
            count++;
        }

        // All layouts should find 3 accounts
        Assert.That(count, Is.EqualTo(3));
    }

    [Test]
    public void TestAccountIterator_EmptyState_ReturnsNoAccounts()
    {
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        using IPersistence.IFlatIterator iterator = reader.CreateAccountIterator();

        int count = 0;
        while (iterator.MoveNext())
        {
            count++;
        }

        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public void TestStorageIterator_EnumeratesAccountStorage()
    {
        // Preimage layouts use raw address, others use hashed address paths
        if (IsPreimage(configuration.FlatDbConfig.Layout))
            Assert.Ignore("Preimage mode uses raw address format which differs from hashed mode");

        // Write account with storage
        Address addr = TestItem.AddressA;
        Account acc = TestItem.GenerateIndexedAccount(0);

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(addr, acc);
            writer.SetStorage(addr, 1, BaseFlatPersistence.DecodeSlotValue([0x11]));
            writer.SetStorage(addr, 42, BaseFlatPersistence.DecodeSlotValue([0x42]));
            writer.SetStorage(addr, 100, BaseFlatPersistence.DecodeSlotValue([0x64]));
        }

        // Use iterator to enumerate storage
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();

        // Storage keys are written using addr.ToAccountPath (Keccak hash of address)
        ValueHash256 accountKey = addr.ToAccountPath;

        using IPersistence.IFlatIterator iterator = reader.CreateStorageIterator(accountKey);

        int count = 0;
        while (iterator.MoveNext())
        {
            count++;
        }

        // Should find 3 storage slots
        Assert.That(count, Is.EqualTo(3));
    }

    [Test]
    public void TestStorageIterator_NoStorage_ReturnsEmpty()
    {
        if (IsPreimage(configuration.FlatDbConfig.Layout))
            Assert.Ignore("Preimage mode uses raw address format which differs from hashed mode");

        // Write account without storage
        Address addr = TestItem.AddressA;
        Account acc = TestItem.GenerateIndexedAccount(0);

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(addr, acc);
        }

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();

        ValueHash256 accountKey = addr.ToAccountPath;

        using IPersistence.IFlatIterator iterator = reader.CreateStorageIterator(accountKey);

        int count = 0;
        while (iterator.MoveNext())
        {
            count++;
        }

        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public void TestStorageIterator_IsolatesAccountStorage()
    {
        if (IsPreimage(configuration.FlatDbConfig.Layout))
            Assert.Ignore("Preimage mode uses raw address format which differs from hashed mode");

        // Write storage for two accounts
        Address addr1 = TestItem.AddressA;
        Address addr2 = TestItem.AddressB;

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(addr1, TestItem.GenerateIndexedAccount(0));
            writer.SetStorage(addr1, 1, BaseFlatPersistence.DecodeSlotValue([0x11]));
            writer.SetStorage(addr1, 2, BaseFlatPersistence.DecodeSlotValue([0x22]));

            writer.SetAccount(addr2, TestItem.GenerateIndexedAccount(1));
            writer.SetStorage(addr2, 10, BaseFlatPersistence.DecodeSlotValue([0xaa]));
            writer.SetStorage(addr2, 20, BaseFlatPersistence.DecodeSlotValue([0xbb]));
            writer.SetStorage(addr2, 30, BaseFlatPersistence.DecodeSlotValue([0xcc]));
        }

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();

        // Count storage for addr1 using proper address hash
        ValueHash256 accountKey1 = addr1.ToAccountPath;
        using IPersistence.IFlatIterator iterator1 = reader.CreateStorageIterator(accountKey1);
        int count1 = 0;
        while (iterator1.MoveNext()) count1++;

        // Count storage for addr2 using proper address hash
        ValueHash256 accountKey2 = addr2.ToAccountPath;
        using IPersistence.IFlatIterator iterator2 = reader.CreateStorageIterator(accountKey2);
        int count2 = 0;
        while (iterator2.MoveNext()) count2++;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(count1, Is.EqualTo(2));
            Assert.That(count2, Is.EqualTo(3));
        }
    }

    [Test]
    public void TestIsPreimageMode_ReturnsCorrectValue()
    {
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();

        // Preimage layouts should return true, others false
        bool expected = IsPreimage(configuration.FlatDbConfig.Layout);
        Assert.That(reader.IsPreimageMode, Is.EqualTo(expected));
    }

    [Test]
    public void SupportsPrefetch_IsTrueOnlyForHashedRocksDbLayouts()
    {
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();

        // Preimage keys are not hashed, so the hashed prefetch does not apply to them.
        // PaprikaFlat does not prefetch.
        Assert.That(reader is IPersistence.IPrefetchingReader { SupportsPrefetch: true }, Is.EqualTo(configuration.FlatDbConfig.Layout is FlatLayout.Flat or FlatLayout.FlatInTrie));
    }

    [Test]
    public void TestRangeDeletes_AreEndInclusive()
    {
        if (IsPreimage(configuration.FlatDbConfig.Layout)) Assert.Ignore("Preimage mode does not support range deletes");

        ValueHash256[] accounts = [PathOf("0x10"), PathOf("0x20"), PathOf("0x30"), PathOf("0x40")];
        // Past the end bound only in the last 12 bytes, which account ranges do not compare.
        accounts[2].BytesAsSpan[^1] = 0xff;
        ValueHash256[] slots = [PathOf("0x10"), PathOf("0x20"), PathOf("0x30"), PathOf("0x40")];
        using (IPersistence.IWriteBatch writer = SyncBatch())
        {
            for (int i = 0; i < accounts.Length; i++)
            {
                writer.SetAccountRaw(accounts[i], TestItem.GenerateIndexedAccount(i));
                writer.SetStorageRawEncoded(accounts[0], slots[i], SlotRlp((byte)(i + 1)));
            }
        }

        using (IPersistence.IWriteBatch writer = SyncBatch())
        {
            writer.DeleteAccountRange(PathOf("0x20"), PathOf("0x30"));
            writer.DeleteStorageRange(accounts[0], slots[1], slots[2]);
        }

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accounts.Select(account => reader.GetAccountRaw(account) is not null), Is.EqualTo(new[] { true, false, false, true }));
            Assert.That(slots.Select(slot => ReadSlotRaw(reader, accounts[0], slot)), Is.EqualTo(new UInt256?[] { 1, null, null, 4 }));
        }
    }

    [Test]
    public void TestSelfDestructRange_ClearsStorageOfAccountsInRange()
    {
        if (IsPreimage(configuration.FlatDbConfig.Layout)) Assert.Ignore("Preimage mode does not support range deletes");

        ValueHash256 orphan = PathOf("0x28");
        ValueHash256 last = PathOf("0x30");
        // Past the end bound only in the last 12 bytes, which account ranges do not compare.
        last.BytesAsSpan[^1] = 0xff;
        ValueHash256[] owners = [PathOf("0x10"), PathOf("0x20"), orphan, last, PathOf("0x40")];
        ValueHash256 slot = PathOf("0x01");
        using (IPersistence.IWriteBatch writer = SyncBatch())
        {
            foreach (ValueHash256 owner in owners)
            {
                // Storage without its account, as a snap sync can leave it, is deleted too.
                if (owner != orphan) writer.SetAccountRaw(owner, TestItem.GenerateIndexedAccount(0));
                writer.SetStorageRawEncoded(owner, slot, SlotRlp(1));
            }
        }

        using (IPersistence.IWriteBatch writer = SyncBatch())
        {
            writer.SelfDestructRange(PathOf("0x20"), PathOf("0x30"));
        }

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(owners.Select(owner => ReadSlotRaw(reader, owner, slot) is not null), Is.EqualTo(new[] { true, false, false, false, true }));
            Assert.That(owners.Select(owner => reader.GetAccountRaw(owner) is not null), Is.EqualTo(new[] { true, true, false, true, true }), "accounts are kept");
        }
    }

    [Test]
    public void TestRangeDeletes_ActOnThePreBatchStateAndLaterPutsSurvive()
    {
        if (IsPreimage(configuration.FlatDbConfig.Layout)) Assert.Ignore("Preimage mode does not support range deletes");

        ValueHash256 other = PathOf("0x10");
        ValueHash256 account = PathOf("0x20");
        ValueHash256 contract = PathOf("0x30");
        ValueHash256[] owners = [account, contract];
        ValueHash256 slot = PathOf("0x01");
        ValueHash256 otherSlot = PathOf("0x02");
        using (IPersistence.IWriteBatch writer = SyncBatch())
        {
            writer.SetAccountRaw(other, TestItem.GenerateIndexedAccount(0));
            writer.SetAccountRaw(account, TestItem.GenerateIndexedAccount(0));
            foreach (ValueHash256 owner in owners)
            {
                writer.SetStorageRawEncoded(owner, slot, SlotRlp(1));
                writer.SetStorageRawEncoded(owner, otherSlot, SlotRlp(1));
            }
        }

        using (IPersistence.IWriteBatch writer = SyncBatch())
        {
            writer.DeleteAccountRange(ValueKeccak.Zero, ValueKeccak.MaxValue);
            writer.SetAccountRaw(account, TestItem.GenerateIndexedAccount(1));
            writer.DeleteStorageRange(account, ValueKeccak.Zero, ValueKeccak.MaxValue);
            writer.SetStorageRawEncoded(account, slot, SlotRlp(2));
            writer.SelfDestructRange(contract, contract);
            writer.SetStorageRawEncoded(contract, slot, SlotRlp(2));
        }

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.GetAccountRaw(other), Is.Null);
            Assert.That(ReadAccountRaw(reader, account), Is.EqualTo(TestItem.GenerateIndexedAccount(1)));
            foreach (ValueHash256 owner in owners)
            {
                Assert.That(ReadSlotRaw(reader, owner, slot), Is.EqualTo((UInt256?)2), $"{owner} slot written after the delete");
                Assert.That(ReadSlotRaw(reader, owner, otherSlot), Is.Null, $"{owner} slot written before the delete");
            }
        }
    }

    [Test]
    public void TestReopenKeepsCommittedState()
    {
        StateId state = new(1, ValueKeccak.Compute("root"));
        Account account = TestItem.GenerateIndexedAccount(0);
        TreePath path = TreePath.FromHexString("12345");
        byte[] node = Bytes.FromHexString("0xc101");
        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, state, WriteFlags.None))
        {
            writer.SetAccount(TestItem.AddressA, account);
            writer.SetStorage(TestItem.AddressA, 42, BaseFlatPersistence.DecodeSlotValue(Bytes.FromHexString("0x42")));
            writer.SetStateTrieNode(path, node);
        }

        _container.Dispose();
        Open();

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.CurrentState, Is.EqualTo(state));
            Assert.That(reader.GetAccount(TestItem.AddressA), Is.EqualTo(account));
            Assert.That(GetSlot(reader, TestItem.AddressA, 42), Is.EqualTo(Bytes.FromHexString("0x42")));
            Assert.That(reader.TryLoadStateRlp(path, ReadFlags.None), Is.EqualTo(node));
        }
    }

    [Test]
    public void TestPaprikaFlatPageCacheBoundsTheMappedFile()
    {
        if (configuration.FlatDbConfig.Layout != FlatLayout.PaprikaFlat || !OperatingSystem.IsLinux())
            Assert.Ignore("Only PaprikaFlat maps its file, and only Linux releases its pages");

        Random random = new(1);
        (Address Address, Account Account)[] accounts = [.. Enumerable.Range(0, 20_000).Select(index => (TestItem.GetRandomAddress(random), TestItem.GenerateIndexedAccount(index)))];
        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            foreach ((Address address, Account account) in accounts) writer.SetAccount(address, account);
        }

        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        {
            Assert.That(accounts.Count(entry => !entry.Account.Equals(reader.GetAccount(entry.Address))), Is.Zero);
        }

        // The minimum of 8 windows a budget keeps, the root pages', and slack for windows read back before their release.
        long bound = 16L * PaprikaPageResidencyAdvisor.UnitBytes;
        string paprikaFile = MemoryMappedPageManager.GetPaprikaFilePath(Path.Combine(_tmpDirectory.Path, "flatPaprika"));
        if (configuration.FlatDbConfig.PaprikaFlatPageCacheBytes < bound)
            Assert.That(() => PaprikaPageResidencyAdvisorTests.Rss(paprikaFile), Is.LessThanOrEqualTo(bound).After(30_000, 100));
        else
            Assert.That(PaprikaPageResidencyAdvisorTests.Rss(paprikaFile), Is.GreaterThan(bound), "the same writes exceed the bound under a larger budget");
    }

    /// <summary>A sync batch, which snap sync issues its range deletes in.</summary>
    private IPersistence.IWriteBatch SyncBatch() => _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);

    /// <summary>A path of the given leading bytes, zero after them.</summary>
    private static ValueHash256 PathOf(string hexPrefix) => new(hexPrefix.PadRight(66, '0'));

    private static byte[] SlotRlp(byte value) => Rlp.Encode(new byte[] { value }).Bytes;

    private static Account? ReadAccountRaw(IPersistence.IPersistenceReader reader, in ValueHash256 path)
    {
        if (reader.GetAccountRaw(path) is not { } rlp) return null;
        RlpReader rlpReader = new(rlp);
        return AccountDecoder.Slim.Decode(ref rlpReader);
    }

    private static UInt256? ReadSlotRaw(IPersistence.IPersistenceReader reader, in ValueHash256 account, in ValueHash256 slot)
    {
        UInt256 value = default;
        return reader.TryGetStorageRaw(account, slot, ref value) ? value : null;
    }
}
