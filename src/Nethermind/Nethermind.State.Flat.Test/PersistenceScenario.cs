// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Autofac;
using FluentAssertions;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Init.Modules;
using Nethermind.Int256;
using Nethermind.Logging;
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
}
