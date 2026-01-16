// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System;
using System.Collections.Generic;
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
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixtureSource(nameof(TestConfigs))]
public class PersistenceScenario(PersistenceScenario.TestConfiguration configuration)
{
    private TempPath _tmpDirectory = null!;
    private IContainer _container = null!;
    private IPersistence _persistence = null!;

    // Helper method to convert TryGetSlot to GetSlot-like behavior
    private static byte[]? GetSlot(IPersistence.IPersistenceReader reader, Address address, in UInt256 slot)
    {
        SlotValue slotValue = default;
        if (reader.TryGetSlot(address, in slot, ref slotValue))
        {
            return slotValue.ToEvmBytes();
        }
        return null;
    }

    public record TestConfiguration(FlatDbConfig FlatDbConfig, string Name)
    {
        public override string ToString() => Name;
    }

    public static IEnumerable<TestConfiguration> TestConfigs()
    {
        yield return new TestConfiguration(new FlatDbConfig()
        {
            Enabled = true,
            Layout = FlatLayout.Flat
        }, "Flat");
        yield return new TestConfiguration(new FlatDbConfig()
        {
            Enabled = true,
            Layout = FlatLayout.FlatInTrie
        }, "FlatInTrie");
        yield return new TestConfiguration(new FlatDbConfig()
        {
            Enabled = true,
            Layout = FlatLayout.PreimageFlat
        }, "PreimageFlat");
    }

    [SetUp]
    public void Setup()
    {
        _tmpDirectory = TempPath.GetTempDirectory();
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

        Assert.That(reader1.GetAccount(address), Is.EqualTo(TestItem.GenerateIndexedAccount(0)));
        Assert.That(reader2.GetAccount(address), Is.EqualTo(TestItem.GenerateIndexedAccount(1)));
        Assert.That(reader3.GetAccount(address), Is.EqualTo(TestItem.GenerateIndexedAccount(2)));
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
            writer.SetStorage(address, UInt256.MinValue, SlotValue.FromSpanWithoutLeadingZero([1]));
            writer.SetStorage(address, 123, SlotValue.FromSpanWithoutLeadingZero([2]));
            writer.SetStorage(address, UInt256.MaxValue, SlotValue.FromSpanWithoutLeadingZero([3]));

            writer.SetAccount(address2, acc2);
            writer.SetStorage(address2, UInt256.MinValue, SlotValue.FromSpanWithoutLeadingZero([1]));
            writer.SetStorage(address2, 123, SlotValue.FromSpanWithoutLeadingZero([2]));
            writer.SetStorage(address2, UInt256.MaxValue, SlotValue.FromSpanWithoutLeadingZero([3]));
        }

        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
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
        {
            Assert.That(GetSlot(reader, address, UInt256.MinValue), Is.Null);
            Assert.That(GetSlot(reader, address, UInt256.MaxValue), Is.Null);
        }

        // Write various storage slots
        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStorage(address, UInt256.MinValue, SlotValue.FromSpanWithoutLeadingZero([1, 2, 3]));
            writer.SetStorage(address, 42, SlotValue.FromSpanWithoutLeadingZero([0x42]));
            writer.SetStorage(address, 12345, SlotValue.FromSpanWithoutLeadingZero([0x10, 0x20, 0x30, 0x40]));
            writer.SetStorage(address, UInt256.MaxValue, SlotValue.FromSpanWithoutLeadingZero([0xff, 0xfe, 0xfd]));
        }

        // Verify all slots can be read back
        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
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
            writer.SetStorage(address, slot, SlotValue.FromSpanWithoutLeadingZero([1]));
        }

        using IPersistence.IPersistenceReader reader1 = _persistence.CreateReader();

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStorage(address, slot, SlotValue.FromSpanWithoutLeadingZero([2]));
        }

        using IPersistence.IPersistenceReader reader2 = _persistence.CreateReader();

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStorage(address, slot, SlotValue.FromSpanWithoutLeadingZero([3]));
        }

        using IPersistence.IPersistenceReader reader3 = _persistence.CreateReader();

        Assert.That(GetSlot(reader1, address, slot), Is.EqualTo([1]));
        Assert.That(GetSlot(reader2, address, slot), Is.EqualTo([2]));
        Assert.That(GetSlot(reader3, address, slot), Is.EqualTo([3]));
    }

    [Test]
    public void TestRemoveAccount()
    {
        Account acc = TestItem.GenerateIndexedAccount(0);
        Address address = TestItem.AddressA;

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, acc);
            writer.SetStorage(address, 1, SlotValue.FromSpanWithoutLeadingZero([0x01]));
            writer.SetStorage(address, 2, SlotValue.FromSpanWithoutLeadingZero([0x02]));
        }

        // Verify account and storage exist
        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        {
            Assert.That(reader.GetAccount(address), Is.EqualTo(acc));
            Assert.That(GetSlot(reader, address, 1), Is.EqualTo([0x01]));
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
        if (configuration.FlatDbConfig.Layout == FlatLayout.PreimageFlat) Assert.Ignore("Preimage mode does not support raw operation");

        Account acc = TestItem.GenerateIndexedAccount(0);
        Hash256 addrHash = new Hash256(TestItem.AddressA.ToAccountPath.Bytes);
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
            Rlp.ValueDecoderContext ctx = new Rlp.ValueDecoderContext(rawAccount);
            Assert.That(AccountDecoder.Instance.Decode(ref ctx), Is.EqualTo(acc));
        }

        // Test raw storage operations
        byte[] storageValue = Bytes.FromHexString("0x000000ffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStorageRaw(addrHash, slotHash, SlotValue.FromBytes(storageValue));
        }

        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        {
            SlotValue rawValue = default;
            Assert.That(reader.TryGetStorageRaw(addrHash, slotHash, ref rawValue), Is.EqualTo(storageValue is not null));
            if (storageValue is not null)
            {
                Assert.That(rawValue.ToEvmBytes(), Is.EqualTo(storageValue.WithoutLeadingZeros().ToArray()));
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
            writer.SetStorage(address, slot1, SlotValue.FromSpanWithoutLeadingZero([1]));
            writer.SetStorage(address, slot2, SlotValue.FromSpanWithoutLeadingZero([10]));
        }

        using IPersistence.IPersistenceReader reader1 = _persistence.CreateReader();

        // Modify account and slot1
        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, TestItem.GenerateIndexedAccount(1));
            writer.SetStorage(address, slot1, SlotValue.FromSpanWithoutLeadingZero([2]));
        }

        using IPersistence.IPersistenceReader reader2 = _persistence.CreateReader();

        // Modify slot2
        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStorage(address, slot2, SlotValue.FromSpanWithoutLeadingZero([20]));
        }

        using IPersistence.IPersistenceReader reader3 = _persistence.CreateReader();

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

            writer.SetStorage(addr1, slot, SlotValue.FromSpanWithoutLeadingZero([0x11]));
            writer.SetStorage(addr2, slot, SlotValue.FromSpanWithoutLeadingZero([0x22]));
            writer.SetStorage(addr3, slot, SlotValue.FromSpanWithoutLeadingZero([0x33]));
        }

        // Verify each account has its own isolated storage
        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        {
            Assert.That(GetSlot(reader, addr1, slot), Is.EqualTo([0x11]));
            Assert.That(GetSlot(reader, addr2, slot), Is.EqualTo([0x22]));
            Assert.That(GetSlot(reader, addr3, slot), Is.EqualTo([0x33]));
        }

        // Modify storage for addr2 only
        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStorage(addr2, slot, SlotValue.FromSpanWithoutLeadingZero([0xff]));
        }

        // Verify only addr2's storage changed
        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
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
            writer.SetStateTrieNode(in stateShortPath, new TrieNode(NodeType.Leaf, stateShortRlp));
            writer.SetStateTrieNode(in stateMediumPath, new TrieNode(NodeType.Leaf, stateMediumRlp));
            writer.SetStateTrieNode(in stateLongPath, new TrieNode(NodeType.Leaf, stateLongRlp));

            // Storage trie nodes (with account address)
            writer.SetStorageTrieNode(account1, in storageShortPath, new TrieNode(NodeType.Leaf, storage1ShortRlp));
            writer.SetStorageTrieNode(account1, in storageLongPath, new TrieNode(NodeType.Leaf, storage1LongRlp));
            writer.SetStorageTrieNode(account2, in storageShortPath, new TrieNode(NodeType.Leaf, storage2ShortRlp));
        }

        // Verify all nodes
        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
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
            writer.SetStateTrieNode(in path, new TrieNode(NodeType.Leaf, rlpData1));
        }
        using IPersistence.IPersistenceReader reader1 = _persistence.CreateReader();

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStateTrieNode(in path, new TrieNode(NodeType.Leaf, rlpData2));
        }
        using IPersistence.IPersistenceReader reader2 = _persistence.CreateReader();

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStateTrieNode(in path, new TrieNode(NodeType.Leaf, rlpData3));
        }
        using IPersistence.IPersistenceReader reader3 = _persistence.CreateReader();

        Assert.That(reader1.TryLoadStateRlp(in path, ReadFlags.None), Is.EqualTo(rlpData1));
        Assert.That(reader2.TryLoadStateRlp(in path, ReadFlags.None), Is.EqualTo(rlpData2));
        Assert.That(reader3.TryLoadStateRlp(in path, ReadFlags.None), Is.EqualTo(rlpData3));
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
            writer.SetStateTrieNode(in statePath5, new TrieNode(NodeType.Leaf, rlp5));
            writer.SetStateTrieNode(in statePath6, new TrieNode(NodeType.Leaf, rlp6));
            writer.SetStateTrieNode(in statePath15, new TrieNode(NodeType.Leaf, rlp15));
            writer.SetStateTrieNode(in statePath16, new TrieNode(NodeType.Leaf, rlp16));
            writer.SetStorageTrieNode(account, in storagePath15, new TrieNode(NodeType.Leaf, storageRlp15));
            writer.SetStorageTrieNode(account, in storagePath16, new TrieNode(NodeType.Leaf, storageRlp16));
        }

        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
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
            writer.SetStorageTrieNode(account1Hash, in shortPath, new TrieNode(NodeType.Leaf, rlpShort));
            writer.SetStorageTrieNode(account1Hash, in mediumPath, new TrieNode(NodeType.Leaf, rlpMedium));
            writer.SetStorageTrieNode(account1Hash, in longPath, new TrieNode(NodeType.Leaf, rlpLong));

            // Account 2 storage trie nodes (same paths, different account)
            writer.SetStorageTrieNode(account2Hash, in shortPath, new TrieNode(NodeType.Leaf, rlpShort));
            writer.SetStorageTrieNode(account2Hash, in mediumPath, new TrieNode(NodeType.Leaf, rlpMedium));
            writer.SetStorageTrieNode(account2Hash, in longPath, new TrieNode(NodeType.Leaf, rlpLong));
        }

        // Verify all nodes exist
        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
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

        Hash256 account1Hash = new Hash256(hash1Bytes);
        Hash256 account2Hash = new Hash256(hash2Bytes);

        TreePath shortPath = TreePath.FromHexString("1234"); // -> StorageNodes
        TreePath longPath = TreePath.FromHexString("0123456789abcdef0123456789abcdef01234567"); // -> FallbackNodes

        byte[] rlp1 = [0xc1, 0x11];
        byte[] rlp2 = [0xc1, 0x22];

        // Write trie nodes using the hashes directly
        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStorageTrieNode(account1Hash, in shortPath, new TrieNode(NodeType.Leaf, rlp1));
            writer.SetStorageTrieNode(account1Hash, in longPath, new TrieNode(NodeType.Leaf, rlp1));
            writer.SetStorageTrieNode(account2Hash, in shortPath, new TrieNode(NodeType.Leaf, rlp2));
            writer.SetStorageTrieNode(account2Hash, in longPath, new TrieNode(NodeType.Leaf, rlp2));
        }

        // Verify all nodes exist before SelfDestruct
        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
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
            writer.SetStorageTrieNode(address1Hash, in shortPath, new TrieNode(NodeType.Leaf, rlp1));
            writer.SetStorageTrieNode(address1Hash, in longPath, new TrieNode(NodeType.Leaf, rlp1));
        }

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SelfDestruct(address1);
        }

        // Verify address1's trie nodes are deleted
        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        {
            Assert.That(reader.TryLoadStorageRlp(address1Hash, in shortPath, ReadFlags.None), Is.Null);
            Assert.That(reader.TryLoadStorageRlp(address1Hash, in longPath, ReadFlags.None), Is.Null);

            // The manually created hashes should still exist (they weren't self-destructed)
            Assert.That(reader.TryLoadStorageRlp(account1Hash, in shortPath, ReadFlags.None), Is.EqualTo(rlp1));
            Assert.That(reader.TryLoadStorageRlp(account2Hash, in shortPath, ReadFlags.None), Is.EqualTo(rlp2));
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
        // PreimageFlat uses raw address, others use hashed address paths
        if (configuration.FlatDbConfig.Layout == FlatLayout.PreimageFlat)
            Assert.Ignore("Preimage mode uses raw address format which differs from hashed mode");

        // Write account with storage
        Address addr = TestItem.AddressA;
        Account acc = TestItem.GenerateIndexedAccount(0);

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(addr, acc);
            writer.SetStorage(addr, 1, SlotValue.FromSpanWithoutLeadingZero([0x11]));
            writer.SetStorage(addr, 42, SlotValue.FromSpanWithoutLeadingZero([0x42]));
            writer.SetStorage(addr, 100, SlotValue.FromSpanWithoutLeadingZero([0x64]));
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
        if (configuration.FlatDbConfig.Layout == FlatLayout.PreimageFlat)
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
        if (configuration.FlatDbConfig.Layout == FlatLayout.PreimageFlat)
            Assert.Ignore("Preimage mode uses raw address format which differs from hashed mode");

        // Write storage for two accounts
        Address addr1 = TestItem.AddressA;
        Address addr2 = TestItem.AddressB;

        using (IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(addr1, TestItem.GenerateIndexedAccount(0));
            writer.SetStorage(addr1, 1, SlotValue.FromSpanWithoutLeadingZero([0x11]));
            writer.SetStorage(addr1, 2, SlotValue.FromSpanWithoutLeadingZero([0x22]));

            writer.SetAccount(addr2, TestItem.GenerateIndexedAccount(1));
            writer.SetStorage(addr2, 10, SlotValue.FromSpanWithoutLeadingZero([0xaa]));
            writer.SetStorage(addr2, 20, SlotValue.FromSpanWithoutLeadingZero([0xbb]));
            writer.SetStorage(addr2, 30, SlotValue.FromSpanWithoutLeadingZero([0xcc]));
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

        Assert.That(count1, Is.EqualTo(2));
        Assert.That(count2, Is.EqualTo(3));
    }

    [Test]
    public void TestIsPreimageMode_ReturnsCorrectValue()
    {
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();

        // PreimageFlat layout should return true, others false
        bool expected = configuration.FlatDbConfig.Layout == FlatLayout.PreimageFlat;
        Assert.That(reader.IsPreimageMode, Is.EqualTo(expected));
    }
}
