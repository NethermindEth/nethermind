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
        yield return new TestConfiguration(new FlatDbConfig()
        {
            Enabled = true,
            Layout = FlatLayout.LMDBFlat
        }, "LMDBFlat");
        yield return new TestConfiguration(new FlatDbConfig()
        {
            Enabled = true,
            Layout = FlatLayout.SmallKeyLMDBFlat
        }, "SmallKeyLMDBFlat");
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
            Account? account = reader.GetAccount(address);
            Assert.That(account, Is.Null);
        }

        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, acc);
        }

        using (var reader = _persistence.CreateReader())
        {
            Account? account = reader.GetAccount(address);
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

        Account? acc1 = reader1.GetAccount(address);
        Assert.That(acc1, Is.EqualTo(TestItem.GenerateIndexedAccount(0)));

        Account? acc2 = reader2.GetAccount(address);
        Assert.That(acc2, Is.EqualTo(TestItem.GenerateIndexedAccount(1)));

        Account? acc3 = reader3.GetAccount(address);
        Assert.That(acc3, Is.EqualTo(TestItem.GenerateIndexedAccount(2)));
    }

    [Test]
    public void TestSelfDestructAccount()
    {
        // TODO: Check other contract are not deleted
        Account acc = TestItem.GenerateIndexedAccount(0);
        Address address = TestItem.AddressA;

        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, acc);
            writer.SetStorage(address, UInt256.MinValue, SlotValue.FromSpanWithoutLeadingZero([1]));
            writer.SetStorage(address, 123, SlotValue.FromSpanWithoutLeadingZero([2]));
            writer.SetStorage(address, UInt256.MaxValue, SlotValue.FromSpanWithoutLeadingZero([3]));
        }

        using (var reader = _persistence.CreateReader())
        {
            byte[]? value = GetSlot(reader, address, UInt256.MinValue);
            Assert.That(value, Is.EqualTo([1]));
            value = GetSlot(reader, address, 123);
            Assert.That(value, Is.EqualTo([2]));
            value = GetSlot(reader, address, UInt256.MaxValue);
            Assert.That(value, Is.EqualTo([3]));
        }

        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SelfDestruct(address);
        }

        using (var reader = _persistence.CreateReader())
        {
            byte[]? value = GetSlot(reader, address, UInt256.MinValue);
            Assert.That(value, Is.Null);
            value = GetSlot(reader, address, 123);
            Assert.That(value, Is.Null);
            value = GetSlot(reader, address, UInt256.MaxValue);
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
            byte[]? value = GetSlot(reader, address, UInt256.MinValue);
            Assert.That(value, Is.Null);
            value = GetSlot(reader, address, UInt256.MaxValue);
            Assert.That(value, Is.Null);
        }

        // Write various storage slots
        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStorage(address, UInt256.MinValue, SlotValue.FromSpanWithoutLeadingZero([1, 2, 3]));
            writer.SetStorage(address, 42, SlotValue.FromSpanWithoutLeadingZero([0x42]));
            writer.SetStorage(address, 12345, SlotValue.FromSpanWithoutLeadingZero([0x10, 0x20, 0x30, 0x40]));
            writer.SetStorage(address, UInt256.MaxValue, SlotValue.FromSpanWithoutLeadingZero([0xff, 0xfe, 0xfd]));
        }

        // Verify all slots can be read back
        using (var reader = _persistence.CreateReader())
        {
            byte[]? value = GetSlot(reader, address, UInt256.MinValue);
            Assert.That(value, Is.EqualTo([1, 2, 3]));

            value = GetSlot(reader, address, 42);
            Assert.That(value, Is.EqualTo([0x42]));

            value = GetSlot(reader, address, 12345);
            Assert.That(value, Is.EqualTo([0x10, 0x20, 0x30, 0x40]));

            value = GetSlot(reader, address, UInt256.MaxValue);
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
            writer.SetStorage(address, slot, SlotValue.FromSpanWithoutLeadingZero([1]));
        }

        using var reader1 = _persistence.CreateReader();

        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStorage(address, slot, SlotValue.FromSpanWithoutLeadingZero([2]));
        }

        using var reader2 = _persistence.CreateReader();

        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStorage(address, slot, SlotValue.FromSpanWithoutLeadingZero([3]));
        }

        using var reader3 = _persistence.CreateReader();

        byte[]? value1 = GetSlot(reader1, address, slot);
        Assert.That(value1, Is.EqualTo([1]));

        byte[]? value2 = GetSlot(reader2, address, slot);
        Assert.That(value2, Is.EqualTo([2]));

        byte[]? value3 = GetSlot(reader3, address, slot);
        Assert.That(value3, Is.EqualTo([3]));
    }

    [Test]
    public void TestRemoveAccount()
    {
        Account acc = TestItem.GenerateIndexedAccount(0);
        Address address = TestItem.AddressA;

        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, acc);
            writer.SetStorage(address, 1, SlotValue.FromSpanWithoutLeadingZero([0x01]));
            writer.SetStorage(address, 2, SlotValue.FromSpanWithoutLeadingZero([0x02]));
        }

        // Verify account and storage exist
        using (var reader = _persistence.CreateReader())
        {
            Account? account = reader.GetAccount(address);
            Assert.That(account, Is.EqualTo(acc));
            byte[]? value = GetSlot(reader, address, 1);
            Assert.That(value, Is.EqualTo([0x01]));
        }

        // Remove account
        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, null);
        }

        // Verify account is removed (storage should remain unless explicitly removed)
        using (var reader = _persistence.CreateReader())
        {
            Account? account = reader.GetAccount(address);
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
        byte[] storageValue = Bytes.FromHexString("0x000000ffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");

        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStorageRaw(addrHash, slotHash, SlotValue.FromBytes(storageValue));
        }

        using (var reader = _persistence.CreateReader())
        {
            byte[]? rawStorage = reader.GetStorageRaw(addrHash, slotHash);
            Assert.That(rawStorage, Is.Not.Null);
            Assert.That(rawStorage, Is.EqualTo(storageValue?.WithoutLeadingZeros().ToArray()));
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
            writer.SetStorage(address, slot1, SlotValue.FromSpanWithoutLeadingZero([1]));
            writer.SetStorage(address, slot2, SlotValue.FromSpanWithoutLeadingZero([10]));
        }

        using var reader1 = _persistence.CreateReader();

        // Modify account and slot1
        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, TestItem.GenerateIndexedAccount(1));
            writer.SetStorage(address, slot1, SlotValue.FromSpanWithoutLeadingZero([2]));
        }

        using var reader2 = _persistence.CreateReader();

        // Modify slot2
        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStorage(address, slot2, SlotValue.FromSpanWithoutLeadingZero([20]));
        }

        using var reader3 = _persistence.CreateReader();

        // Verify reader1 sees initial state
        Account? acc1 = reader1.GetAccount(address);
        Assert.That(acc1, Is.EqualTo(acc));
        byte[]? val1 = GetSlot(reader1, address, slot1);
        Assert.That(val1, Is.EqualTo([1]));
        val1 = GetSlot(reader1, address, slot2);
        Assert.That(val1, Is.EqualTo([10]));

        // Verify reader2 sees second state
        Account? acc2 = reader2.GetAccount(address);
        Assert.That(acc2, Is.EqualTo(TestItem.GenerateIndexedAccount(1)));
        byte[]? val2 = GetSlot(reader2, address, slot1);
        Assert.That(val2, Is.EqualTo([2]));
        val2 = GetSlot(reader2, address, slot2);
        Assert.That(val2, Is.EqualTo([10]));

        // Verify reader3 sees final state
        Account? acc3 = reader3.GetAccount(address);
        Assert.That(acc3, Is.EqualTo(TestItem.GenerateIndexedAccount(1)));
        byte[]? val3 = GetSlot(reader3, address, slot1);
        Assert.That(val3, Is.EqualTo([2]));
        val3 = GetSlot(reader3, address, slot2);
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

            writer.SetStorage(addr1, slot, SlotValue.FromSpanWithoutLeadingZero([0x11]));
            writer.SetStorage(addr2, slot, SlotValue.FromSpanWithoutLeadingZero([0x22]));
            writer.SetStorage(addr3, slot, SlotValue.FromSpanWithoutLeadingZero([0x33]));
        }

        // Verify each account has its own isolated storage
        using (var reader = _persistence.CreateReader())
        {
            byte[]? val1 = GetSlot(reader, addr1, slot);
            Assert.That(val1, Is.EqualTo([0x11]));

            byte[]? val2 = GetSlot(reader, addr2, slot);
            Assert.That(val2, Is.EqualTo([0x22]));

            byte[]? val3 = GetSlot(reader, addr3, slot);
            Assert.That(val3, Is.EqualTo([0x33]));
        }

        // Modify storage for addr2 only
        using (var writer = _persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStorage(addr2, slot, SlotValue.FromSpanWithoutLeadingZero([0xff]));
        }

        // Verify only addr2's storage changed
        using (var reader = _persistence.CreateReader())
        {
            byte[]? val1 = GetSlot(reader, addr1, slot);
            Assert.That(val1, Is.EqualTo([0x11]));

            byte[]? val2 = GetSlot(reader, addr2, slot);
            Assert.That(val2, Is.EqualTo([0xff]));

            byte[]? val3 = GetSlot(reader, addr3, slot);
            Assert.That(val3, Is.EqualTo([0x33]));
        }
    }
}
