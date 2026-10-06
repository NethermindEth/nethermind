// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Pbt.Migration;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace Nethermind.State.Pbt.Test;

/// <summary>Restarts over RocksDB: both native backends are flushed before shutdown and the node resumes on what they hold.</summary>
[TestFixture, NonParallelizable]
public class MigrationRestartE2ETests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp() => _directory = CreateTestDirectory();

    [TearDown]
    public void TearDown()
    {
        if (TestContext.CurrentContext.Result.Outcome.Status == TestStatus.Passed) Directory.Delete(_directory, recursive: true);
        else TestContext.Out.WriteLine($"Retained failed restart datadir: {_directory}");
    }

    [Test]
    public async Task Restart_before_and_after_activation_resumes_on_the_persisted_backends([Values] bool portable)
    {
        await using (MigrationLifecycleHarness harness = await OpenLifecycle(_directory, portable))
        {
            ProcessBranch(harness, ["a1", "a2", "a3"]);
            Promote(harness, "a3");
            Persist(harness);
        }
        await using (MigrationLifecycleHarness reopened = await OpenLifecycle(_directory, portable))
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(reopened.Tree.Head!.Hash, Is.EqualTo(reopened.Blocks["a3"].Hash));
                Assert.That(reopened.Reader.HasStateForBlock(reopened.Blocks["a3"].Header), Is.True);
                Assert.That(reopened.Telemetry.GetShadowRoot(reopened.Blocks["a3"].Hash!), Is.EqualTo(reopened.PbtRoot("a3")));
                Assert.That(reopened.Telemetry.GetProgress().Binary!.Phase, Is.EqualTo("synced"));
            }
            ProcessBranch(reopened, ["a4", "a5"]);
            Promote(reopened, "a5");
            Persist(reopened);
        }
        await using MigrationLifecycleHarness final = await OpenLifecycle(_directory, portable);
        using IPersistence.IPersistenceReader flat = final.Container.Resolve<IPersistence>().CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(final.Tree.Head!.Hash, Is.EqualTo(final.Blocks["a5"].Hash));
            Assert.That(final.Reader.HasStateForBlock(final.Blocks["a5"].Header), Is.True);
            Assert.That(() => final.Telemetry.GetShadowRoot(final.Blocks["a5"].Hash!), Is.EqualTo(final.ExpectedShadowRoot("a5")).After(10_000, 50),
                "the Merkle shadow is rebuilt from the persisted flat state");
            Assert.That(flat.CurrentState, Is.EqualTo(new Flat.StateId(final.Blocks["a3"].Header)), "flat persists up to the activation parent and no further");
            final.AssertAllocation("a5");
        }
    }

    [Test]
    public async Task Anchor_imported_behind_the_flat_head_is_caught_up_from_stored_bals()
    {
        // Flat alone runs the chain first, as a node that enables the migration later would have.
        await RunFlatOnly("a1", "a2", "a3");
        await using MigrationLifecycleHarness migrating = await OpenLifecycle(_directory, portable: true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(migrating.Tree.Head!.Hash, Is.EqualTo(migrating.Blocks["a3"].Hash));
            Assert.That(migrating.Telemetry.GetShadowRoot(migrating.Anchor.Hash!), Is.EqualTo(migrating.PbtRoot("anchor")));
            Assert.That(() => migrating.Pbt.HasStateForBlock(new StateId(migrating.Blocks["a3"].Header)), Is.True.After(30_000, 100), migrating.Scheduler.Error);
            Assert.That(migrating.Telemetry.GetShadowRoot(migrating.Blocks["a3"].Hash!), Is.EqualTo(migrating.PbtRoot("a3")));
            Assert.That(migrating.Reader.HasStateForBlock(migrating.Blocks["a3"].Header), Is.True);
        }
    }

    [Test]
    public async Task Flat_processes_alone_while_pbt_lacks_the_base()
    {
        await RunFlatOnly("a1", "a2");
        // A follower that never runs keeps PBT at the anchor, behind the flat head.
        await using MigrationLifecycleHarness migrating = await MigrationLifecycleHarness.Create(Path.Combine(_directory, "db"), portable: true, FlatLayout.Flat,
            Path.Combine(Eip8347FixtureState.Directory, "builder-predeploys"),
            builder => ConfigureRocks(builder).AddSingleton<PbtBalFollowerScheduler>(_ => new PbtBalFollowerScheduler((_, _) => Task.FromResult(false), () => null, () => null)));
        ProcessBranch(migrating, ["a3"], expectPbt: false);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(migrating.Pbt.HasStateForBlock(new StateId(migrating.Blocks["a2"].Header)), Is.False);
            Assert.That(migrating.Pbt.HasStateForBlock(new StateId(migrating.Blocks["a3"].Header)), Is.False, "a3 ran on flat alone");
            Assert.That(migrating.Reader.HasStateForBlock(migrating.Blocks["a3"].Header), Is.True);
        }
    }

    private async Task RunFlatOnly(params string[] names)
    {
        await using MigrationLifecycleHarness flatOnly = await MigrationLifecycleHarness.Create(Path.Combine(_directory, "db"), portable: true, FlatLayout.Flat,
            Path.Combine(Eip8347FixtureState.Directory, "builder-predeploys"), builder => ConfigureRocks(builder), migration: false);
        ProcessBranch(flatOnly, names, expectPbt: false);
        Promote(flatOnly, names[^1]);
        Persist(flatOnly);
    }

    private static string CreateTestDirectory()
    {
        string? root = TestContext.CurrentContext.TestDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "global.json"))) root = Path.GetDirectoryName(root);
        if (root is null) throw new InvalidOperationException("Run restart tests from a repository build output.");
        string directory = Path.Combine(root, ".tmp", "eip8347-restart", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static ContainerBuilder ConfigureRocks(ContainerBuilder builder) => builder
        .AddSingleton<IDbFactory, RocksDbFactory>()
        .AddSingleton<IColumnsDb<FlatDbColumns>>(context => context.Resolve<IDbFactory>()
            .CreateColumnsDb<FlatDbColumns>(new DbSettings("Flat", "flat")));

    private static Task<MigrationLifecycleHarness> OpenLifecycle(string directory, bool portable)
        => MigrationLifecycleHarness.Create(Path.Combine(directory, "db"), portable, FlatLayout.Flat,
            Path.Combine(Eip8347FixtureState.Directory, "builder-predeploys"), builder => ConfigureRocks(builder));

    private static void ProcessBranch(MigrationLifecycleHarness harness, string[] names, bool expectPbt = true)
    {
        IContainer container = harness.Container;
        IBlockAccessListStore bals = container.Resolve<IBlockAccessListStore>();
        Block[] branch = new Block[names.Length];
        for (int index = 0; index < names.Length; index++)
        {
            Block block = harness.Blocks[names[index]];
            branch[index] = block;
            block.EncodedBlockAccessList = Bytes.FromHexString(harness.Expected[names[index]].GetProperty("balRlp").GetString()!);
            block.BlockAccessList = Rlp.Decode<ReadOnlyBlockAccessList>(block.EncodedBlockAccessList);
            bals.Insert(block.Number, block.Hash!, block.EncodedBlockAccessList);
            foreach (IBlockPreprocessorStep preprocessor in container.Resolve<IReadOnlyList<IBlockPreprocessorStep>>()) preprocessor.RecoverData(block);
            harness.Tree.SuggestBlock(block);
        }
        IBlockchainProcessor processor = container.Resolve<IMainProcessingContext>().BlockchainProcessor;
        foreach (Block block in branch)
        {
            if (expectPbt && harness.Expected[names[Array.IndexOf(branch, block)]].GetProperty("binary").GetBoolean())
                harness.WaitForPbt(harness.Tree.FindHeader(block.ParentHash!, BlockTreeLookupOptions.None)!);
            Assert.That(processor.Process(block, ProcessingOptions.EthereumMerge | ProcessingOptions.StoreReceipts,
                NullBlockTracer.Instance)?.Hash, Is.EqualTo(block.Hash));
        }
        if (!expectPbt) return;
        foreach (string name in names)
        {
            bool binary = harness.Expected[name].GetProperty("binary").GetBoolean();
            Assert.That(() => harness.Telemetry.GetShadowRoot(harness.Blocks[name].Hash!), Is.EqualTo(binary ? null : harness.PbtRoot(name)).After(30_000, 50), name);
        }
    }

    private static void Promote(MigrationLifecycleHarness harness, string name)
    {
        Block block = harness.Blocks[name];
        Assert.That(harness.Tree.TryUpdateMainChain(block.Header, true, true), Is.True);
    }

    // A clean shutdown no longer flushes the unfinalized tail, so the persisted state is reached explicitly.
    private static void Persist(MigrationLifecycleHarness harness) =>
        harness.Container.Resolve<IWorldStateManager>().FlushCache(CancellationToken.None);
}
