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
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Db.FullPruning;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.FullPruning
{
    public class FullPruningDiskTest
    {
        public class PruningTestBlockchain : TestBlockchain
        {
            private readonly IPruningConfig _pruningConfig;
            public IFullPruningDb PruningDb => Container.Resolve<IFullPruningDb>();
            public TempPath TempDirectory { get; }
            public IPruningTrigger PruningTrigger { get; } = Substitute.For<IPruningTrigger>();
            public FullTestPruner FullPruner => Container.Resolve<FullTestPruner>();

            public PruningTestBlockchain(IPruningConfig pruningConfig)
            {
                TempDirectory = TempPath.GetTempDirectory();
                _pruningConfig = pruningConfig;
            }

            protected override async Task<TestBlockchain> Build(
                ISpecProvider? specProvider = null,
                UInt256? initialValues = null,
                bool addBlockOnStart = true
            )
            {
                TestBlockchain chain = await base.Build(specProvider, initialValues, addBlockOnStart);

                // Needed so that it listen to event and start
                _ = chain.Container.Resolve<FullTestPruner>();
                return chain;
            }

            protected override void ConfigureContainer(ContainerBuilder builder)
            {
                base.ConfigureContainer(builder);

                IDriveInfo mockDriveInfo = Substitute.For<IDriveInfo>();
                mockDriveInfo.AvailableFreeSpace.Returns(long.MaxValue);
                builder.RegisterInstance(mockDriveInfo).As<IDriveInfo>();

                IChainEstimations chainEstimations = Substitute.For<IChainEstimations>();
                chainEstimations.StateSize.Returns((long?)null);
                builder.RegisterInstance(chainEstimations).As<IChainEstimations>();

                builder.RegisterInstance(_pruningConfig).As<IPruningConfig>();
                builder.RegisterInstance(PruningTrigger).As<IPruningTrigger>();

                builder.RegisterInstance<IDbFactory>(new RocksDbFactory(new DbConfig(), LogManager, TempDirectory.Path));
                builder.RegisterType<FullTestPruner>().AsSelf().SingleInstance();
            }

            public override void Dispose()
            {
                base.Dispose();
                TempDirectory.Dispose();
            }

            protected override Task AddBlocksOnStart() => Task.CompletedTask;

            public static async Task<PruningTestBlockchain> Create(IPruningConfig? pruningConfig = null)
            {
                pruningConfig ??= new PruningConfig();
                pruningConfig.Mode = PruningMode.Full;
                PruningTestBlockchain chain = new(pruningConfig);
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

        [Test, Timeout(Timeout.MaxTestTime), Retry(5)]
        public async Task prune_on_disk_multiple_times()
        {
            using PruningTestBlockchain chain = await PruningTestBlockchain.Create(new PruningConfig { FullPruningMinimumDelayHours = 0 });
            for (int i = 0; i < 3; i++)
            {
                await RunPruning(chain, i, false);
            }
        }

        [Test, Timeout(Timeout.MaxTestTime), Retry(5)]
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
            chain.Container.Resolve<IChainEstimations>().PruningSize.Returns(requiredSpace);
            chain.Container.Resolve<IDriveInfo>().AvailableFreeSpace.Returns(availableSpace);

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
                await chain.AddBlock(true);
            }

            HashSet<byte[]> allItems = chain.DbProvider.StateDb.GetAllValues().ToHashSet(Bytes.EqualityComparer);
            bool pruningFinished = false;
            for (int i = 0; i < 100 && !pruningFinished; i++)
            {
                pruningFinished = chain.FullPruner.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(100));
                await chain.AddBlock(true);
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
}
