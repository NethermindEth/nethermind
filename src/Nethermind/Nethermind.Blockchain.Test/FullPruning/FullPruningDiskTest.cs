//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.FullPruning;
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
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.FullPruning
{
    public class FullPruningDiskTest
    {
        public class PruningTestBlockchain : TestBlockchain
        {
            public IFullPruningDb PruningDb { get; private set; }
            public TempPath TempDirectory { get; }
            public IPruningTrigger PruningTrigger { get; } = Substitute.For<IPruningTrigger>();
            public FullTestPruner FullPruner { get; private set; }
            public IPruningConfig PruningConfig { get; set; } = new PruningConfig();

            public PruningTestBlockchain()
            {
                TempDirectory = TempPath.GetTempDirectory();
            }

            protected override async Task<TestBlockchain> Build(ISpecProvider specProvider = null, UInt256? initialValues = null)
            {
                TestBlockchain chain = await base.Build(specProvider, initialValues);
                PruningDb = (IFullPruningDb)DbProvider.StateDb;
                FullPruner = new FullTestPruner(PruningDb, PruningTrigger, PruningConfig, BlockTree, StateReader, LogManager);
                return chain;
            }

            protected override async Task<IDbProvider> CreateDbProvider()
            {
                IDbProvider dbProvider = new DbProvider(DbModeHint.Persisted);
                RocksDbFactory rocksDbFactory = new(new DbConfig(), LogManager, TempDirectory.Path);
                StandardDbInitializer standardDbInitializer = new(dbProvider, rocksDbFactory, new MemDbFactory(), new FileSystem(), true);
                await standardDbInitializer.InitStandardDbsAsync(true);
                return dbProvider;
            }

            public override void Dispose()
            {
                base.Dispose();
                TempDirectory.Dispose();
            }

            protected override Task AddBlocksOnStart() => Task.CompletedTask;

            public static async Task<PruningTestBlockchain> Create(IPruningConfig pruningConfig = null)
            {
                PruningTestBlockchain chain = new() { PruningConfig = pruningConfig ?? new PruningConfig() };
                await chain.Build();
                return chain;
            }
            
            public class FullTestPruner : FullPruner
            {
                public EventWaitHandle WaitHandle { get; } = new ManualResetEvent(false); 
                
                public FullTestPruner(
                    IFullPruningDb pruningDb,
                    IPruningTrigger pruningTrigger,
                    IPruningConfig pruningConfig,
                    IBlockTree blockTree,
                    IStateReader stateReader,
                    ILogManager logManager)
                    : base(pruningDb, pruningTrigger, pruningConfig, blockTree, stateReader, logManager)
                {
                }

                protected override void RunPruning(IPruningContext pruning, Keccak stateRoot)
                {
                    base.RunPruning(pruning, stateRoot);
                    WaitHandle.Set();
                }
            }
        }
            
        [Test]
        public async Task prune_on_disk_multiple_times()
        {
            using PruningTestBlockchain chain = await PruningTestBlockchain.Create(new PruningConfig { FullPruningMinimumDelayHours = 0 });
            for (int i = 0; i < 3; i++)
            {
                await RunPruning(chain, i, false);
            }
        }
        
        [Test]
        public async Task prune_on_disk_only_once()
        {
            using PruningTestBlockchain chain = await PruningTestBlockchain.Create(new PruningConfig { FullPruningMinimumDelayHours = 10 });
            for (int i = 0; i < 3; i++)
            {
                await RunPruning(chain, i, true);
            }
        }

        private static async Task RunPruning(PruningTestBlockchain chain, int time, bool onlyFirstRuns)
        {
            chain.FullPruner.WaitHandle.Reset();
            chain.PruningTrigger.Prune += Raise.Event<EventHandler<PruningTriggerEventArgs>>();
            for (int i = 0; i < Reorganization.MaxDepth + 2; i++)
            {
                await chain.AddBlock(true);
            }

            HashSet<byte[]> allItems = chain.DbProvider.StateDb.GetAllValues().ToHashSet(Bytes.EqualityComparer);
            bool pruningFinished = chain.FullPruner.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
            
            await chain.AddBlock(true);

            if (!onlyFirstRuns || time == 0)
            {
                pruningFinished.Should().BeTrue();

                await WriteFileStructure(chain);

                chain.PruningDb.InnerDbName.Should().Be($"State{time + 1}");

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
