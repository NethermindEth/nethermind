// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Autofac;
using FluentAssertions;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixtureSource(nameof(TestConfigs))]
public class PersistenceScenario(PersistenceScenario.TestConfiguration configuration)
{
    private TempPath _tmpDirectory = null!;
    private IContainer _container = null!;
    private IPersistence _persistence = null!;

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
        yield return new TestConfiguration(new FlatDbConfig()
        {
            Enabled = true,
            Layout = FlatLayout.LMDBFlat
        }, "LMDBFlat");
    }


    [SetUp]
    public void Setup()
    {
        _tmpDirectory = TempPath.GetTempDirectory();
        _container = new ContainerBuilder()
            .AddModule(new NethermindModule(
                new ChainSpec(),
                new ConfigProvider(
                    configuration.FlatDbConfig,
                    new InitConfig()
                    {
                        BaseDbPath = _tmpDirectory.Path,
                    }),
                LimboLogs.Instance))
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

        using (var reader = _persistence.CreateReader())
        {
            Assert.That(reader.TryGetAccount(address, out Account? account), Is.True);
            Assert.That(account, Is.Null);
        }

        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, acc);
        }

        using (var reader = _persistence.CreateReader())
        {
            Assert.That(reader.TryGetAccount(address, out Account? account), Is.True);
            Assert.That(account, Is.EqualTo(acc));
        }
    }

    [Test]
    public void TestCanAccountSnapshot()
    {
        Address address = TestItem.AddressA;

        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, TestItem.GenerateIndexedAccount(0));
        }

        using var reader1 = _persistence.CreateReader();

        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, TestItem.GenerateIndexedAccount(1));
        }

        using var reader2 = _persistence.CreateReader();

        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, TestItem.GenerateIndexedAccount(2));
        }

        using var reader3 = _persistence.CreateReader();

        Assert.That(reader1.TryGetAccount(address, out Account? acc), Is.True);
        Assert.That(acc, Is.EqualTo(TestItem.GenerateIndexedAccount(0)));

        Assert.That(reader2.TryGetAccount(address, out acc), Is.True);
        Assert.That(acc, Is.EqualTo(TestItem.GenerateIndexedAccount(1)));

        Assert.That(reader3.TryGetAccount(address, out acc), Is.True);
        Assert.That(acc, Is.EqualTo(TestItem.GenerateIndexedAccount(2)));
    }

    [Test]
    public void TestSelfDestructAccount()
    {
        Account acc = TestItem.GenerateIndexedAccount(0);
        Address address = TestItem.AddressA;

        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, acc);
            writer.SetStorage(address, UInt256.MinValue, [1]);
            writer.SetStorage(address, 123, [2]);
            writer.SetStorage(address, UInt256.MaxValue, [3]);
        }

        using (var reader = _persistence.CreateReader())
        {
            reader.TryGetSlot(address, UInt256.MinValue, out var value).Should().BeTrue();
            Assert.That(value, Is.EqualTo([1]));
            reader.TryGetSlot(address, 123, out value).Should().BeTrue();
            Assert.That(value, Is.EqualTo([2]));
            reader.TryGetSlot(address, UInt256.MaxValue, out value).Should().BeTrue();
            Assert.That(value, Is.EqualTo([3]));
        }

        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SelfDestruct(address);
        }

        using (var reader = _persistence.CreateReader())
        {
            reader.TryGetSlot(address, UInt256.MinValue, out var value).Should().BeTrue();
            Assert.That(value, Is.Null);
            reader.TryGetSlot(address, 123, out value).Should().BeTrue();
            Assert.That(value, Is.Null);
            reader.TryGetSlot(address, UInt256.MaxValue, out value).Should().BeTrue();
            Assert.That(value, Is.Null);
        }
    }

    [Test]
    public void TestCanWriteAndReadStorage()
    {
        Account acc = TestItem.GenerateIndexedAccount(0);
        Address address = TestItem.AddressA;

        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, acc);
        }

        // Initially, slots should be null
        using (var reader = _persistence.CreateReader())
        {
            Assert.That(reader.TryGetSlot(address, UInt256.MinValue, out var value), Is.True);
            Assert.That(value, Is.Null);
            Assert.That(reader.TryGetSlot(address, UInt256.MaxValue, out value), Is.True);
            Assert.That(value, Is.Null);
        }

        // Write various storage slots
        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStorage(address, UInt256.MinValue, [1, 2, 3]);
            writer.SetStorage(address, 42, [0x42]);
            writer.SetStorage(address, 12345, [0x10, 0x20, 0x30, 0x40]);
            writer.SetStorage(address, UInt256.MaxValue, [0xff, 0xfe, 0xfd]);
        }

        // Verify all slots can be read back
        using (var reader = _persistence.CreateReader())
        {
            Assert.That(reader.TryGetSlot(address, UInt256.MinValue, out var value), Is.True);
            Assert.That(value, Is.EqualTo([1, 2, 3]));

            Assert.That(reader.TryGetSlot(address, 42, out value), Is.True);
            Assert.That(value, Is.EqualTo([0x42]));

            Assert.That(reader.TryGetSlot(address, 12345, out value), Is.True);
            Assert.That(value, Is.EqualTo([0x10, 0x20, 0x30, 0x40]));

            Assert.That(reader.TryGetSlot(address, UInt256.MaxValue, out value), Is.True);
            Assert.That(value, Is.EqualTo([0xff, 0xfe, 0xfd]));
        }
    }

    [Test]
    public void TestCanStorageSnapshot()
    {
        Account acc = TestItem.GenerateIndexedAccount(0);
        Address address = TestItem.AddressA;
        UInt256 slot = 100;

        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, acc);
            writer.SetStorage(address, slot, [1]);
        }

        using var reader1 = _persistence.CreateReader();

        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStorage(address, slot, [2]);
        }

        using var reader2 = _persistence.CreateReader();

        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStorage(address, slot, [3]);
        }

        using var reader3 = _persistence.CreateReader();

        Assert.That(reader1.TryGetSlot(address, slot, out var value), Is.True);
        Assert.That(value, Is.EqualTo([1]));

        Assert.That(reader2.TryGetSlot(address, slot, out value), Is.True);
        Assert.That(value, Is.EqualTo([2]));

        Assert.That(reader3.TryGetSlot(address, slot, out value), Is.True);
        Assert.That(value, Is.EqualTo([3]));
    }

    [Test]
    public void TestRemoveStorage()
    {
        Account acc = TestItem.GenerateIndexedAccount(0);
        Address address = TestItem.AddressA;

        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, acc);
            writer.SetStorage(address, 1, [0x01]);
            writer.SetStorage(address, 2, [0x02]);
            writer.SetStorage(address, 3, [0x03]);
        }

        // Verify all slots exist
        using (var reader = _persistence.CreateReader())
        {
            reader.TryGetSlot(address, 1, out var value).Should().BeTrue();
            Assert.That(value, Is.EqualTo([0x01]));
            reader.TryGetSlot(address, 2, out value).Should().BeTrue();
            Assert.That(value, Is.EqualTo([0x02]));
            reader.TryGetSlot(address, 3, out value).Should().BeTrue();
            Assert.That(value, Is.EqualTo([0x03]));
        }

        // Remove slot 2
        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.RemoveStorage(address, 2);
        }

        // Verify slot 2 is removed but others remain
        using (var reader = _persistence.CreateReader())
        {
            reader.TryGetSlot(address, 1, out var value).Should().BeTrue();
            Assert.That(value, Is.EqualTo([0x01]));

            reader.TryGetSlot(address, 2, out value).Should().BeTrue();
            Assert.That(value, Is.Null);

            reader.TryGetSlot(address, 3, out value).Should().BeTrue();
            Assert.That(value, Is.EqualTo([0x03]));
        }
    }

    [Test]
    public void TestRemoveAccount()
    {
        Account acc = TestItem.GenerateIndexedAccount(0);
        Address address = TestItem.AddressA;

        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, acc);
            writer.SetStorage(address, 1, [0x01]);
            writer.SetStorage(address, 2, [0x02]);
        }

        // Verify account and storage exist
        using (var reader = _persistence.CreateReader())
        {
            reader.TryGetAccount(address, out var account).Should().BeTrue();
            Assert.That(account, Is.EqualTo(acc));
            reader.TryGetSlot(address, 1, out var value).Should().BeTrue();
            Assert.That(value, Is.EqualTo([0x01]));
        }

        // Remove account
        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.RemoveAccount(address);
        }

        // Verify account is removed (storage should remain unless explicitly removed)
        using (var reader = _persistence.CreateReader())
        {
            reader.TryGetAccount(address, out var account).Should().BeTrue();
            Assert.That(account, Is.Null);
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
        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccountRaw(addrHash, acc);
        }

        using (var reader = _persistence.CreateReader())
        {
            byte[]? rawAccount = reader.GetAccountRaw(addrHash);
            Assert.That(rawAccount, Is.Not.Null);

            // Decode and verify
            var ctx = new Rlp.ValueDecoderContext(rawAccount);
            Account? decodedAcc = AccountDecoder.Instance.Decode(ref ctx);
            Assert.That(decodedAcc, Is.EqualTo(acc));
        }

        // Test raw storage operations
        byte[] storageValue = [0xaa, 0xbb, 0xcc];

        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStorageRaw(addrHash, slotHash, storageValue);
        }

        using (var reader = _persistence.CreateReader())
        {
            byte[]? rawStorage = reader.GetStorageRaw(addrHash, slotHash);
            Assert.That(rawStorage, Is.Not.Null);
            Assert.That(rawStorage, Is.EqualTo(storageValue));
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
        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, acc);
            writer.SetStorage(address, slot1, [1]);
            writer.SetStorage(address, slot2, [10]);
        }

        using var reader1 = _persistence.CreateReader();

        // Modify account and slot1
        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, TestItem.GenerateIndexedAccount(1));
            writer.SetStorage(address, slot1, [2]);
        }

        using var reader2 = _persistence.CreateReader();

        // Modify slot2
        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStorage(address, slot2, [20]);
        }

        using var reader3 = _persistence.CreateReader();

        // Verify reader1 sees initial state
        reader1.TryGetAccount(address, out var acc1).Should().BeTrue();
        Assert.That(acc1, Is.EqualTo(acc));
        reader1.TryGetSlot(address, slot1, out var val1).Should().BeTrue();
        Assert.That(val1, Is.EqualTo([1]));
        reader1.TryGetSlot(address, slot2, out val1).Should().BeTrue();
        Assert.That(val1, Is.EqualTo([10]));

        // Verify reader2 sees second state
        reader2.TryGetAccount(address, out var acc2).Should().BeTrue();
        Assert.That(acc2, Is.EqualTo(TestItem.GenerateIndexedAccount(1)));
        reader2.TryGetSlot(address, slot1, out var val2).Should().BeTrue();
        Assert.That(val2, Is.EqualTo([2]));
        reader2.TryGetSlot(address, slot2, out val2).Should().BeTrue();
        Assert.That(val2, Is.EqualTo([10]));

        // Verify reader3 sees final state
        reader3.TryGetAccount(address, out var acc3).Should().BeTrue();
        Assert.That(acc3, Is.EqualTo(TestItem.GenerateIndexedAccount(1)));
        reader3.TryGetSlot(address, slot1, out var val3).Should().BeTrue();
        Assert.That(val3, Is.EqualTo([2]));
        reader3.TryGetSlot(address, slot2, out val3).Should().BeTrue();
        Assert.That(val3, Is.EqualTo([20]));
    }

    [Test]
    public void TestStorageAcrossMultipleAccounts()
    {
        Address addr1 = TestItem.AddressA;
        Address addr2 = TestItem.AddressB;
        Address addr3 = TestItem.AddressC;
        UInt256 slot = 42;

        // Write same slot number for different accounts
        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(addr1, TestItem.GenerateIndexedAccount(0));
            writer.SetAccount(addr2, TestItem.GenerateIndexedAccount(1));
            writer.SetAccount(addr3, TestItem.GenerateIndexedAccount(2));

            writer.SetStorage(addr1, slot, [0x11]);
            writer.SetStorage(addr2, slot, [0x22]);
            writer.SetStorage(addr3, slot, [0x33]);
        }

        // Verify each account has its own isolated storage
        using (var reader = _persistence.CreateReader())
        {
            reader.TryGetSlot(addr1, slot, out var val1).Should().BeTrue();
            Assert.That(val1, Is.EqualTo([0x11]));

            reader.TryGetSlot(addr2, slot, out var val2).Should().BeTrue();
            Assert.That(val2, Is.EqualTo([0x22]));

            reader.TryGetSlot(addr3, slot, out var val3).Should().BeTrue();
            Assert.That(val3, Is.EqualTo([0x33]));
        }

        // Modify storage for addr2 only
        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStorage(addr2, slot, [0xff]);
        }

        // Verify only addr2's storage changed
        using (var reader = _persistence.CreateReader())
        {
            reader.TryGetSlot(addr1, slot, out var val1).Should().BeTrue();
            Assert.That(val1, Is.EqualTo([0x11]));

            reader.TryGetSlot(addr2, slot, out var val2).Should().BeTrue();
            Assert.That(val2, Is.EqualTo([0xff]));

            reader.TryGetSlot(addr3, slot, out var val3).Should().BeTrue();
            Assert.That(val3, Is.EqualTo([0x33]));
        }
    }
}
