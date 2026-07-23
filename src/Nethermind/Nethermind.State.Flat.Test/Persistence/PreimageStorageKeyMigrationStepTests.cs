// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Init.Modules;
using Nethermind.Init.Steps;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State.Flat.Persistence;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Persistence;

/// <summary>
/// Drives the migration through its startup step over a real RocksDB flat DB, which is what exercises the column
/// iteration, the WAL-less write batches and the scratch DB lifecycle that an in-memory DB cannot stand in for.
/// </summary>
[TestFixture]
public class PreimageStorageKeyMigrationStepTests
{
    private static readonly Address Address = TestItem.AddressA;
    private static readonly UInt256[] Slots = [UInt256.Zero, 1, 42, UInt256.MaxValue];

    [Test]
    public async Task Step_MigratesAndExits_LeavingTheDbReadableOnTheNewLayout()
    {
        using TempPath dbPath = TempPath.GetTempDirectory();
        IProcessExitSource exitSource = Substitute.For<IProcessExitSource>();

        using (IContainer container = BuildContainer(dbPath, FlatLayout.PreimageFlatV1, exitSource))
        {
            using (IPersistence.IWriteBatch writeBatch = container.Resolve<IPersistence>()
                .CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
            {
                writeBatch.SetAccount(Address, TestItem.GenerateIndexedAccount(0));
                foreach (UInt256 slot in Slots) writeBatch.SetStorage(Address, slot, SlotValueOf(slot));
            }

            await container.Resolve<MigratePreimageStorageKeys>().Execute(CancellationToken.None);
        }

        using (Assert.EnterMultipleScope())
        {
            exitSource.Received().Exit(0);
            Assert.That(Directory.Exists(Path.Combine(dbPath.Path, "preimageKeyMigration")), Is.False, "scratch DB was not cleaned up");
        }

        // Opening on the new layout only succeeds if the migration stamped it, and only reads back if it converted.
        using (IContainer container = BuildContainer(dbPath, FlatLayout.PreimageFlat, exitSource))
        {
            using IPersistence.IPersistenceReader reader = container.Resolve<IPersistence>().CreateReader();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(reader.GetAccount(Address), Is.EqualTo(TestItem.GenerateIndexedAccount(0)));
                foreach (UInt256 slot in Slots)
                {
                    SlotValue value = default;
                    reader.TryGetSlot(Address, slot, ref value);
                    Assert.That(value, Is.EqualTo(SlotValueOf(slot)), $"slot {slot}");
                }
            }
        }
    }

    [Test]
    public async Task Step_DropsALeftoverScratchDb_WhenThereIsNothingToMigrate()
    {
        using TempPath dbPath = TempPath.GetTempDirectory();
        IProcessExitSource exitSource = Substitute.For<IProcessExitSource>();
        string scratchPath = Path.Combine(dbPath.Path, "preimageKeyMigration");
        Directory.CreateDirectory(scratchPath);

        using IContainer container = BuildContainer(dbPath, FlatLayout.PreimageFlat, exitSource);
        await container.Resolve<MigratePreimageStorageKeys>().Execute(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Directory.Exists(scratchPath), Is.False);
            exitSource.DidNotReceive().Exit(Arg.Any<int>());
        }
    }

    private static SlotValue SlotValueOf(in UInt256 slot) => SlotValue.FromSpanWithoutLeadingZero([1, (byte)slot.u0]);

    private static IContainer BuildContainer(TempPath dbPath, FlatLayout layout, IProcessExitSource exitSource) =>
        new ContainerBuilder()
            .AddModule(new NethermindModule(
                new ChainSpec(),
                new ConfigProvider(
                    new FlatDbConfig
                    {
                        Enabled = true,
                        Layout = layout,
                        MigrateToPreimageFlat = true,
                    },
                    new InitConfig { BaseDbPath = dbPath.Path }),
                LimboLogs.Instance))
            .AddSingleton(exitSource)
            .Build();
}
