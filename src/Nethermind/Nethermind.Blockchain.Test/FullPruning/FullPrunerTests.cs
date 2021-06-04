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
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Db.FullPruning;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NSubstitute.ReceivedExtensions;
using NUnit.Framework;
using Org.BouncyCastle.Asn1.Cms;

namespace Nethermind.Blockchain.Test.FullPruning
{
    [Parallelizable(ParallelScope.All)]
    public class FullPrunerTests
    {
        [Test]
        public async Task can_prune()
        {
            TestContext test = CreateTest();
            bool contextDisposed = await test.WaitForPruning();
            contextDisposed.Should().BeTrue();
        }

        [Test]
        public async Task pruning_deletes_old_db_on_success()
        {
            TestContext test = CreateTest();
            await test.WaitForPruning();
            test.TrieDb.Count.Should().Be(0);
        }
        
        [Test]
        public async Task pruning_keeps_old_db_on_fail()
        {
            TestContext test = CreateTest(false);
            int count = test.TrieDb.Count;
            await test.WaitForPruning();
            test.TrieDb.Count.Should().Be(count);
        }
        
        [Test]
        public async Task pruning_deletes_new_db_on_fail()
        {
            TestContext test = CreateTest(false);
            await test.WaitForPruning();
            test.CopyDb.Count.Should().Be(0);
        }
        
        [Test]
        public async Task pruning_keeps_new_db_on_success()
        {
            TestContext test = CreateTest();
            int count = test.TrieDb.Count;
            await test.WaitForPruning();
            test.CopyDb.Count.Should().Be(count);
        }
        
        [Test]
        public async Task pruning_is_in_progress_during_pruning()
        {
            TestContext test = CreateTest();
            test.FullPruningDb.PruningInProgress.Should().BeFalse();
            
            test.PruningTrigger.Prune += Raise.Event();
            test.FullPruningDb.PruningInProgress.Should().BeTrue();
            test.FullPruningDb.Context.WaitForFinish.Set();
            await test.FullPruningDb.Context.DisposeEvent.WaitOneAsync(TimeSpan.FromMilliseconds(10), CancellationToken.None);
            
            test.FullPruningDb.PruningInProgress.Should().BeFalse();
        }
        
        [Test]
        public async Task should_not_start_multiple_pruning()
        {
            TestContext test = CreateTest();
            test.PruningTrigger.Prune += Raise.Event();
            await test.WaitForPruning();
            test.FullPruningDb.PruningStarted.Should().Be(1);
        }
        
        [Test]
        public async Task should_duplicate_writes_while_pruning()
        {
            TestContext test = CreateTest();
            test.PruningTrigger.Prune += Raise.Event();
            byte[] key = {0, 1, 2};
            test.FullPruningDb[key] = key;
            test.FullPruningDb.Context.WaitForFinish.Set();
            await test.FullPruningDb.Context.DisposeEvent.WaitOneAsync(TimeSpan.FromMilliseconds(10), CancellationToken.None);

            test.FullPruningDb[key].Should().BeEquivalentTo(key);
        }

        private static TestContext CreateTest(bool successfulPruning = true) => new(successfulPruning);

        private class TestContext
        {
            public TestFullPruningDb FullPruningDb { get; }
            public IPruningTrigger PruningTrigger { get; } = Substitute.For<IPruningTrigger>();
            public IBlockTree BlockTree { get; } = Substitute.For<IBlockTree>();
            public IStateReader StateReader { get; }
            public FullPruner Pruner { get; }
            public MemDb TrieDb { get; }
            public MemDb CopyDb { get; }

            public TestContext(bool successfulPruning)
            {
                TrieDb = new MemDb();
                CopyDb = new MemDb();
                IRocksDbFactory rocksDbFactory = Substitute.For<IRocksDbFactory>();
                rocksDbFactory.CreateDb(Arg.Any<RocksDbSettings>()).Returns(TrieDb, CopyDb);
                
                PatriciaTree trie = Build.A.Trie(TrieDb).WithAccountsByIndex(0, 100).TestObject;
                StateReader = new StateReader(new TrieStore(TrieDb, LimboLogs.Instance), new MemDb(), LimboLogs.Instance);
                BlockTree.BestState.Returns(10);
                BlockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(10).TestObject);
                BlockTree.FindHeader(10).Returns(Build.A.BlockHeader.WithStateRoot(trie.RootHash).TestObject);

                FullPruningDb = new TestFullPruningDb(new RocksDbSettings("test", "test"), rocksDbFactory, successfulPruning);
                
                Pruner = new(FullPruningDb, PruningTrigger, BlockTree, StateReader, LimboLogs.Instance);
            }

            public Task<bool> WaitForPruning()
            {
                PruningTrigger.Prune += Raise.Event();
                FullPruningDb.Context.WaitForFinish.Set();
                return FullPruningDb.Context.DisposeEvent.WaitOneAsync(TimeSpan.FromMilliseconds(10), CancellationToken.None);
            }
        }
        
        private class TestFullPruningDb : FullPruningDb
        {
            private readonly bool _successfulPruning;
            
            public TestPruningContext Context { get; set; }
            public int PruningStarted { get; private set; }
            
            public TestFullPruningDb(RocksDbSettings settings, IRocksDbFactory dbFactory, bool successfulPruning) 
                : base(settings, dbFactory)
            {
                _successfulPruning = successfulPruning;
            }

            public override bool TryStartPruning(out IPruningContext context)
            {
                if (base.TryStartPruning(out context))
                {
                    context = Context = new TestPruningContext(context, _successfulPruning);
                    PruningStarted++;
                    return true;
                }
                return false;
            }

            internal class TestPruningContext : IPruningContext
            {
                private readonly IPruningContext _context;
                private readonly bool _successfulPruning;

                public ManualResetEvent DisposeEvent { get; } = new(false);
                public ManualResetEvent WaitForFinish { get; } = new(false);
                
                public TestPruningContext(IPruningContext context, bool successfulPruning)
                {
                    _context = context;
                    _successfulPruning = successfulPruning;
                }

                public void Dispose()
                {
                    _context.Dispose();
                    DisposeEvent.Set();
                }

                public byte[] this[byte[] key]
                {
                    set => _context[key] = value;
                }

                public void Commit()
                {
                    WaitForFinish.WaitOne();
                    if (_successfulPruning)
                    {
                        _context.Commit();
                    }
                }

                public void MarkStart()
                {
                    _context.MarkStart();
                }
            }
        }
    }
}
