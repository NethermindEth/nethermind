// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Pbt.Migration;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

/// <summary>Restarts over RocksDB: both native backends are flushed before shutdown and the node resumes on what they hold.</summary>
[TestFixture, NonParallelizable]
public class MigrationRestartE2ETests
{
    [Test]
    public async Task Restart_before_and_after_activation_resumes_on_the_persisted_backends([Values] bool portable)
    {
        string directory = CreateTestDirectory();
        try
        {
            await using (MigrationLifecycleHarness harness = await OpenLifecycle(directory, portable))
            {
                ProcessBranch(harness, ["a1", "a2", "a3"]);
                Promote(harness, "a3");
                Persist(harness);
            }
            await using (MigrationLifecycleHarness reopened = await OpenLifecycle(directory, portable))
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(reopened.Tree.Head!.Hash, Is.EqualTo(reopened.Blocks["a3"].Hash));
                    Assert.That(reopened.Reader.HasStateForBlock(reopened.Blocks["a3"].Header), Is.True);
                    Assert.That(reopened.Telemetry.GetShadowRoot(reopened.Blocks["a3"].Hash!), Is.EqualTo(PbtRoot(reopened, "a3")));
                    Assert.That(reopened.Telemetry.GetProgress().Binary!.Phase, Is.EqualTo("synced"));
                }
                ProcessBranch(reopened, ["a4", "a5"]);
                Promote(reopened, "a5");
                Persist(reopened);
            }
            await using MigrationLifecycleHarness final = await OpenLifecycle(directory, portable);
            using IPersistence.IPersistenceReader flat = final.Container.Resolve<IPersistence>().CreateReader();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(final.Tree.Head!.Hash, Is.EqualTo(final.Blocks["a5"].Hash));
                Assert.That(final.Reader.HasStateForBlock(final.Blocks["a5"].Header), Is.True);
                Assert.That(() => final.Telemetry.GetShadowRoot(final.Blocks["a5"].Hash!), Is.EqualTo(MigrationLifecycleE2ETests.ExpectedShadowRoot(final, "a5")).After(10_000, 50),
                    "the Merkle shadow is rebuilt from the persisted flat state");
                Assert.That(flat.CurrentState, Is.EqualTo(new Flat.StateId(final.Blocks["a3"].Header)), "flat persists up to the activation parent and no further");
                AssertAllocation(final, "a5");
            }
        }
        catch { TestContext.Out.WriteLine($"Retained failed restart datadir: {directory}"); throw; }
        Directory.Delete(directory, recursive: true);
    }

    [Test]
    public async Task Anchor_imported_behind_the_flat_head_is_caught_up_from_stored_bals()
    {
        string directory = CreateTestDirectory();
        try
        {
            // Flat alone runs the chain first, as a node that enables the migration later would have.
            await using (MigrationLifecycleHarness flatOnly = await MigrationLifecycleHarness.Create(Path.Combine(directory, "db"), portable: true,
                builder => ConfigureRocks(builder), Path.Combine(MigrationLifecycleHarness.Fixtures, "builder-predeploys"), migration: false))
            {
                ProcessBranch(flatOnly, ["a1", "a2", "a3"], expectPbt: false);
                Promote(flatOnly, "a3");
                Persist(flatOnly);
            }
            await using MigrationLifecycleHarness migrating = await OpenLifecycle(directory, portable: true);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(migrating.Tree.Head!.Hash, Is.EqualTo(migrating.Blocks["a3"].Hash));
                Assert.That(migrating.Telemetry.GetShadowRoot(migrating.Anchor.Hash!), Is.EqualTo(PbtRoot(migrating, "anchor")));
                Assert.That(() => migrating.Pbt.HasStateForBlock(new StateId(migrating.Blocks["a3"].Header)), Is.True.After(30_000, 100), migrating.Scheduler.Error);
                Assert.That(migrating.Telemetry.GetShadowRoot(migrating.Blocks["a3"].Hash!), Is.EqualTo(PbtRoot(migrating, "a3")));
                Assert.That(migrating.Reader.HasStateForBlock(migrating.Blocks["a3"].Header), Is.True);
            }
        }
        catch { TestContext.Out.WriteLine($"Retained failed restart datadir: {directory}"); throw; }
        Directory.Delete(directory, recursive: true);
    }

    [Test]
    public async Task External_offline_preimage_database_bootstraps_standard_flat_and_reopens()
    {
        string directory = CreateTestDirectory();
        try
        {
            string producerPath = Path.Combine(directory, "producer");
            string sourcePath = Path.Combine(directory, "offline-source");
            await using (MigrationLifecycleHarness producer = await MigrationLifecycleHarness.Create(producerPath, false, builder => ConfigureRocks(builder)))
            {
                producer.Container.Resolve<MigrationGenesisSource>().Database.Flush();
                producer.Container.Resolve<IDbProvider>().CodeDb.Flush();
            }
            CopyDirectory(Path.Combine(producerPath, "migration-work", "genesis-source"), Path.Combine(sourcePath, "flat"));
            CopyDirectory(Path.Combine(producerPath, "code"), Path.Combine(sourcePath, "code"));
            Dictionary<string, string> originalFiles = HashFiles(sourcePath);
            string target = Path.Combine(directory, "target");
            for (int pass = 0; pass < 2; pass++)
            {
                await using MigrationLifecycleHarness consumer = await MigrationLifecycleHarness.Create(target, true,
                    builder => ConfigureRocks(builder), configureMigration: config =>
                    {
                        config.MigrationSnapshotPath = null;
                        config.MigrationPreimagesPath = null;
                        config.MigrationPreimageSourcePath = sourcePath;
                    });
                using IPersistence.IPersistenceReader native = consumer.Container.Resolve<IPersistence>().CreateReader();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(native.IsPreimageMode, Is.False);
                    Assert.That(native.CurrentState.BlockNumber, Is.Zero);
                    Assert.That(consumer.Telemetry.GetShadowRoot(consumer.Anchor.Hash!), Is.EqualTo(PbtRoot(consumer, "anchor")));
                }
            }
            Assert.That(HashFiles(sourcePath), Is.EquivalentTo(originalFiles), "offline source must remain byte-for-byte unchanged");
        }
        catch { TestContext.Out.WriteLine($"Retained failed restart datadir: {directory}"); throw; }
        Directory.Delete(directory, recursive: true);
    }

    private static Hash256 PbtRoot(MigrationLifecycleHarness harness, string name) => new(harness.Expected[name].GetProperty("pbtRoot").GetString()!);

    private static void AssertAllocation(MigrationLifecycleHarness harness, string name)
    {
        BlockHeader header = harness.Blocks[name].Header;
        using JsonDocument allocation = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(harness.FixtureDirectory, "states", name + ".alloc.json")));
        foreach (JsonProperty account in allocation.RootElement.EnumerateObject())
        {
            string balance = account.Value.GetProperty("balance").GetString()!;
            UInt256 expected = new(Bytes.FromHexString(balance.Length % 2 == 0 ? balance : "0x0" + balance[2..]), true);
            Assert.That(harness.Reader.GetBalance(header, new Address(account.Name)), Is.EqualTo(expected), $"{name} balance {account.Name}");
        }
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

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (string child in Directory.GetDirectories(source)) CopyDirectory(child, Path.Combine(target, Path.GetFileName(child)));
    }

    private static Dictionary<string, string> HashFiles(string directory)
    {
        Dictionary<string, string> result = [];
        foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            result.Add(Path.GetRelativePath(directory, file), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))));
        return result;
    }

    private static ContainerBuilder ConfigureRocks(ContainerBuilder builder) => builder
        .AddSingleton<IDbFactory, RocksDbFactory>()
        .AddSingleton<IColumnsDb<FlatDbColumns>>(context => context.Resolve<IDbFactory>()
            .CreateColumnsDb<FlatDbColumns>(new DbSettings("Flat", "flat")));

    private static Task<MigrationLifecycleHarness> OpenLifecycle(string directory, bool portable)
        => MigrationLifecycleHarness.Create(Path.Combine(directory, "db"), portable, builder => ConfigureRocks(builder),
            Path.Combine(MigrationLifecycleHarness.Fixtures, "builder-predeploys"));

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
            Assert.That(processor.Process(block, ProcessingOptions.EthereumMerge | ProcessingOptions.StoreReceipts,
                NullBlockTracer.Instance)?.Hash, Is.EqualTo(block.Hash));
        if (!expectPbt) return;
        foreach (string name in names)
        {
            bool binary = harness.Expected[name].GetProperty("binary").GetBoolean();
            Assert.That(harness.Telemetry.GetShadowRoot(harness.Blocks[name].Hash!), Is.EqualTo(binary ? null : PbtRoot(harness, name)), name);
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
