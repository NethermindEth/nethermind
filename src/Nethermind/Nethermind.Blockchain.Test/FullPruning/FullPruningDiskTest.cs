// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Db.FullPruning;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.FullPruning;

public class FullPruningDiskTest
{
    public class PruningTestBlockchain : TestBlockchain
    {
        public FullPruningDb PruningDb { get; private set; } = null!;
        public INodeStorage MainNodeStorage { get; private set; } = null!;
        public TempPath TempDirectory { get; }
        public IPruningTrigger PruningTrigger { get; } = Substitute.For<IPruningTrigger>();
        public FullTestPruner FullPruner { get; private set; } = null!;
        public IPruningConfig PruningConfig { get; set; } = new PruningConfig();
        public IDriveInfo DriveInfo { get; set; } = Substitute.For<IDriveInfo>();
        public IChainEstimations _chainEstimations = Substitute.For<IChainEstimations>();
        public IProcessExitSource ProcessExitSource { get; } = Substitute.For<IProcessExitSource>();

        public PruningTestBlockchain()
        {
            TempDirectory = TempPath.GetTempDirectory();
        }

        protected override async Task<TestBlockchain> Build(Action<ContainerBuilder>? containerBuilder = null)
        {
            TestBlockchain chain = await base.Build(containerBuilder);
            PruningDb = (FullPruningDb)DbProvider.StateDb;
            DriveInfo.AvailableFreeSpace.Returns(long.MaxValue);
            _chainEstimations.StateSize.Returns((long?)null);

            NodeStorageFactory nodeStorageFactory = new(INodeStorage.KeyScheme.Current, LimboLogs.Instance);
            MainNodeStorage = nodeStorageFactory.WrapKeyValueStore(PruningDb);

            FullPruner = new FullTestPruner(
                PruningDb,
                nodeStorageFactory,
                MainNodeStorage,
                PruningTrigger,
                PruningConfig,
                BlockTree,
                StateReader,
                ProcessExitSource,
                DriveInfo,
                Container.Resolve<IPruningTrieStore>(),
                _chainEstimations,
                LogManager);
            return chain;
        }

        protected override ContainerBuilder ConfigureContainer(ContainerBuilder builder, IConfigProvider configProvider)
        {
            IDbProvider dbProvider = new DbProvider();
            RocksDbFactory rocksDbFactory = new(new DbConfig(), LogManager, TempDirectory.Path);
            StandardDbInitializer standardDbInitializer = new(dbProvider, rocksDbFactory, new FileSystem());
            standardDbInitializer.InitStandardDbs(true);

            return base.ConfigureContainer(builder, configProvider)
                .AddSingleton<IDbProvider>(dbProvider)
                .ConfigureTrieStoreExposedWorldStateManager();
        }

        public override void Dispose()
        {
            base.Dispose();
            TempDirectory.Dispose();
        }

        protected override Task AddBlocksOnStart() => Task.CompletedTask;

        public static async Task<PruningTestBlockchain> Create(IPruningConfig? pruningConfig = null, long testTimeoutMs = 10000)
        {
            PruningTestBlockchain chain = new()
            {
                PruningConfig = pruningConfig ?? new PruningConfig(),
                TestTimout = testTimeoutMs,
            };
            await chain.Build();
            return chain;
        }

        public class FullTestPruner : FullPruner
        {
            public EventWaitHandle WaitHandle { get; } = new ManualResetEvent(false);

            public FullTestPruner(
                IFullPruningDb pruningDb,
                INodeStorageFactory nodeStorageFactory,
                INodeStorage mainNodeStorage,
                IPruningTrigger pruningTrigger,
                IPruningConfig pruningConfig,
                IBlockTree blockTree,
                IStateReader stateReader,
                IProcessExitSource processExitSource,
                IDriveInfo driveInfo,
                IPruningTrieStore trieStore,
                IChainEstimations chainEstimations,
                ILogManager logManager)
                : base(pruningDb, nodeStorageFactory, mainNodeStorage, pruningTrigger, pruningConfig, blockTree, stateReader, processExitSource, chainEstimations, driveInfo, trieStore, logManager)
            {
            }

            protected override async Task RunFullPruning(CancellationToken cancellationToken)
            {
                await base.RunFullPruning(cancellationToken);
                WaitHandle.Set();
            }
        }
    }

    [Test, MaxTime(Timeout.LongTestTime)]
    public async Task prune_on_disk_multiple_times()
    {
        using PruningTestBlockchain chain = await PruningTestBlockchain.Create(new PruningConfig { FullPruningMinimumDelayHours = 0 }, testTimeoutMs: Timeout.LongTestTime);
        for (int i = 0; i < 3; i++)
        {
            await RunPruning(chain, i, false);
        }
    }

    [Test, MaxTime(Timeout.LongTestTime)]
    public async Task prune_on_disk_only_once()
    {
        using PruningTestBlockchain chain = await PruningTestBlockchain.Create(new PruningConfig { FullPruningMinimumDelayHours = 10 });
        for (int i = 0; i < 3; i++)
        {
            await RunPruning(chain, i, true);
        }
    }

    [TestCase(100, 150, false)]
    [TestCase(200, 100, true)]
    [TestCase(130, 100, true)]
    [TestCase(130, 101, false)]
    public async Task should_check_available_space_before_running(long availableSpace, long requiredSpace, bool isEnoughSpace)
    {
        using PruningTestBlockchain chain = await PruningTestBlockchain.Create();
        chain._chainEstimations.PruningSize.Returns(requiredSpace);
        chain.DriveInfo.AvailableFreeSpace.Returns(availableSpace);
        PruningTriggerEventArgs args = new();
        chain.PruningTrigger.Prune += Raise.Event<EventHandler<PruningTriggerEventArgs>>(args);
        args.Status.Should().Be(isEnoughSpace ? PruningStatus.Starting : PruningStatus.NotEnoughDiskSpace);
    }

    private static async Task RunPruning(PruningTestBlockchain chain, int time, bool onlyFirstRuns)
    {
        chain.FullPruner.WaitHandle.Reset();
        PruningTriggerEventArgs args = new();
        chain.PruningTrigger.Prune += Raise.Event<EventHandler<PruningTriggerEventArgs>>(args);
        if (args.Status != PruningStatus.Starting) return;
        for (int i = 0; i < Reorganization.MaxDepth + 2; i++)
        {
            await chain.AddBlock();
        }

        HashSet<byte[]> allItems = chain.DbProvider.StateDb.GetAllValues().ToHashSet(Bytes.EqualityComparer);
        bool pruningFinished = false;
        for (int i = 0; i < 100 && !pruningFinished; i++)
        {
            pruningFinished = chain.FullPruner.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(100));
            await chain.AddBlockDoNotWaitForHead();
        }

        if (!onlyFirstRuns || time == 0)
        {
            pruningFinished.Should().BeTrue();

            await WriteFileStructure(chain);

            Assert.That(
                () => chain.PruningDb.InnerDbName,
                Is.EqualTo($"State{time + 1}").After(500, 100)
                );

            HashSet<byte[]> currentItems = chain.DbProvider.StateDb.GetAllValues().ToHashSet(Bytes.EqualityComparer);
            currentItems.IsSubsetOf(allItems).Should().BeTrue();
            currentItems.Count.Should().BeGreaterThan(0);
        }
    }

    private static async Task WriteFileStructure(PruningTestBlockchain chain)
    {
        string stateDbPath = Path.Combine(chain.TempDirectory.Path, "state");
        foreach (string directory in Directory.EnumerateDirectories(stateDbPath))
        {
            await TestContext.Out.WriteLineAsync(directory);
        }

        foreach (string file in Directory.EnumerateFiles(stateDbPath))
        {
            await TestContext.Out.WriteLineAsync(file);
        }
    }
}
