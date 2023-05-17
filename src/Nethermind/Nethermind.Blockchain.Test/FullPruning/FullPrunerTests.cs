// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Db.FullPruning;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.FullPruning
{

    [TestFixture(0, 1)]
    [TestFixture(0, 4)]
    [TestFixture(1, 1)]
    [TestFixture(1, 4)]
    [Parallelizable(ParallelScope.All)]
    public class FullPrunerTests
    {
        private readonly int _fullPrunerMemoryBudgetMb;
        private readonly int _degreeOfParallelism;

        public FullPrunerTests(int fullPrunerMemoryBudgetMb, int degreeOfParallelism)
        {
            _fullPrunerMemoryBudgetMb = fullPrunerMemoryBudgetMb;
            _degreeOfParallelism = degreeOfParallelism;
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public async Task can_prune()
        {
            TestContext test = CreateTest();
            bool contextDisposed = await test.WaitForPruning();
            contextDisposed.Should().BeTrue();
            test.ShouldCopyAllValues();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public async Task pruning_deletes_old_db_on_success()
        {
            TestContext test = CreateTest(clearPrunedDb: true);
            await test.WaitForPruning();
            test.TrieDb.Count.Should().Be(0);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public async Task pruning_keeps_old_db_on_fail()
        {
            TestContext test = CreateTest(false);
            int count = test.TrieDb.Count;
            await test.WaitForPruning();
            test.TrieDb.Count.Should().Be(count);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public async Task pruning_deletes_new_db_on_fail()
        {
            TestContext test = CreateTest(false);
            await test.WaitForPruning();
            test.CopyDb.Count.Should().Be(0);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public async Task pruning_keeps_new_db_on_success()
        {
            TestContext test = CreateTest();
            int count = test.TrieDb.Count;
            bool result = await test.WaitForPruning();
            result.Should().BeTrue();
            test.CopyDb.Count.Should().Be(count);
        }

        [Timeout(Timeout.MaxTestTime)]
        [TestCase(true, FullPruningCompletionBehavior.None, false)]
        [TestCase(true, FullPruningCompletionBehavior.ShutdownOnSuccess, true)]
        [TestCase(true, FullPruningCompletionBehavior.AlwaysShutdown, true)]
        [TestCase(false, FullPruningCompletionBehavior.None, false)]
        [TestCase(false, FullPruningCompletionBehavior.ShutdownOnSuccess, false)]
        [TestCase(false, FullPruningCompletionBehavior.AlwaysShutdown, true)]
        public async Task pruning_shuts_down_node(bool success, FullPruningCompletionBehavior behavior, bool expectedShutdown)
        {
            TestContext test = CreateTest(successfulPruning: success, completionBehavior: behavior);
            await test.WaitForPruning();

            if (expectedShutdown)
            {
                test.ProcessExitSource.Received(1).Exit(ExitCodes.Ok);
            }
            else
            {
                test.ProcessExitSource.DidNotReceiveWithAnyArgs().Exit(ExitCodes.Ok);
            }
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public async Task can_not_start_pruning_when_other_is_in_progress()
        {
            TestContext test = CreateTest();
            test.FullPruningDb.CanStartPruning.Should().BeTrue();

            var pruningContext = test.WaitForPruningStart();
            test.FullPruningDb.CanStartPruning.Should().BeFalse();
            await test.WaitForPruningEnd(pruningContext);

            test.FullPruningDb.CanStartPruning.Should().BeTrue();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public async Task should_not_start_multiple_pruning()
        {
            TestContext test = CreateTest();
            test.PruningTrigger.Prune += Raise.Event<EventHandler<PruningTriggerEventArgs>>();
            await test.WaitForPruning();
            test.FullPruningDb.PruningStarted.Should().Be(1);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public async Task should_duplicate_writes_while_pruning()
        {
            TestContext test = CreateTest();
            test.WaitForPruningStart();
            byte[] key = { 1, 2, 3 };
            test.FullPruningDb[key] = key;
            test.FullPruningDb.Context.WaitForFinish.Set();
            await test.FullPruningDb.Context.DisposeEvent.WaitOneAsync(TimeSpan.FromMilliseconds(Timeout.MaxWaitTime), CancellationToken.None);

            test.FullPruningDb[key].Should().BeEquivalentTo(key);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public async Task should_duplicate_writes_to_batches_while_pruning()
        {
            TestContext test = CreateTest();
            byte[] key = { 0, 1, 2 };
            TestFullPruningDb.TestPruningContext context = test.WaitForPruningStart();

            using (IBatch batch = test.FullPruningDb.StartBatch())
            {
                batch[key] = key;
            }

            await test.WaitForPruningEnd(context);

            test.FullPruningDb[key].Should().BeEquivalentTo(key);
        }

        private TestContext CreateTest(bool successfulPruning = true, bool clearPrunedDb = false, FullPruningCompletionBehavior completionBehavior = FullPruningCompletionBehavior.None) =>
            new(successfulPruning, clearPrunedDb, completionBehavior, _fullPrunerMemoryBudgetMb, _degreeOfParallelism);

        private class TestContext
        {
            private readonly bool _clearPrunedDb;
            private readonly Keccak _stateRoot;
            private long _head;
            public TestFullPruningDb FullPruningDb { get; }
            public IPruningTrigger PruningTrigger { get; } = Substitute.For<IPruningTrigger>();
            public IBlockTree BlockTree { get; } = Substitute.For<IBlockTree>();
            public IStateReader StateReader { get; }
            public FullPruner Pruner { get; }
            public MemDb TrieDb { get; }
            public TestMemDb CopyDb { get; }
            public IDriveInfo DriveInfo { get; set; } = Substitute.For<IDriveInfo>();
            public IChainEstimations _chainEstimations = ChainSizes.UnknownChain.Instance;

            public IProcessExitSource ProcessExitSource { get; } = Substitute.For<IProcessExitSource>();

            public TestContext(
                bool successfulPruning,
                bool clearPrunedDb = false,
                FullPruningCompletionBehavior completionBehavior = FullPruningCompletionBehavior.None,
                int fullScanMemoryBudgetMb = 0,
                int degreeOfParallelism = 0)
            {
                BlockTree.OnUpdateMainChain += (_, e) => _head = e.Blocks[^1].Number;
                _clearPrunedDb = clearPrunedDb;
                TrieDb = new TestMemDb();
                CopyDb = new TestMemDb();
                IRocksDbFactory rocksDbFactory = Substitute.For<IRocksDbFactory>();
                rocksDbFactory.CreateDb(Arg.Any<RocksDbSettings>()).Returns(TrieDb, CopyDb);

                PatriciaTree trie = Build.A.Trie(TrieDb).WithAccountsByIndex(0, 100).TestObject;
                _stateRoot = trie.RootHash;
                StateReader = new StateReader(new TrieStore(TrieDb, LimboLogs.Instance), new TestMemDb(), LimboLogs.Instance);
                FullPruningDb = new TestFullPruningDb(new RocksDbSettings("test", "test"), rocksDbFactory, successfulPruning, clearPrunedDb);

                Pruner = new(FullPruningDb, PruningTrigger, new PruningConfig()
                {
                    FullPruningMaxDegreeOfParallelism = degreeOfParallelism,
                    FullPruningMemoryBudgetMb = fullScanMemoryBudgetMb,
                    FullPruningCompletionBehavior = completionBehavior
                }, BlockTree, StateReader, ProcessExitSource, _chainEstimations, DriveInfo, LimboLogs.Instance);
            }

            public async Task<bool> WaitForPruning()
            {
                TestFullPruningDb.TestPruningContext context = WaitForPruningStart();
                bool result = await WaitForPruningEnd(context);
                if (result && _clearPrunedDb)
                {
                    await FullPruningDb.WaitForClearDb.WaitOneAsync(TimeSpan.FromMilliseconds(Timeout.MaxWaitTime * 3), CancellationToken.None);
                }

                return result;
            }

            public async Task<bool> WaitForPruningEnd(TestFullPruningDb.TestPruningContext context)
            {
                await Task.Yield();
                await context.WaitForFinish.WaitOneAsync(TimeSpan.FromMilliseconds(Timeout.MaxWaitTime), CancellationToken.None);
                AddBlocks(1);
                return await context.DisposeEvent.WaitOneAsync(TimeSpan.FromMilliseconds(Timeout.MaxWaitTime), CancellationToken.None);
            }

            public TestFullPruningDb.TestPruningContext WaitForPruningStart()
            {
                PruningTrigger.Prune += Raise.Event<EventHandler<PruningTriggerEventArgs>>();
                AddBlocks(Reorganization.MaxDepth + 2);
                TestFullPruningDb.TestPruningContext context = FullPruningDb.Context;
                return context;
            }

            public void AddBlocks(long count)
            {
                for (int i = 0; i < count; i++)
                {
                    long number = _head + 1;
                    BlockTree.BestPersistedState.Returns(_head);
                    Block head = Build.A.Block.WithStateRoot(_stateRoot).WithNumber(number).TestObject;
                    BlockTree.Head.Returns(head);
                    BlockTree.FindHeader(number).Returns(head.Header);
                    BlockTree.OnUpdateMainChain += Raise.EventWith(new OnUpdateMainChainArgs(new List<Block>() { head }, true));
                }
            }

            public void ShouldCopyAllValues()
            {
                foreach (KeyValuePair<byte[], byte[]?> keyValuePair in TrieDb.GetAll())
                {
                    CopyDb[keyValuePair.Key].Should().BeEquivalentTo(keyValuePair.Value);
                    CopyDb.KeyWasWrittenWithFlags(keyValuePair.Key, WriteFlags.LowPriority | WriteFlags.DisableWAL);
                }
            }
        }

        private class TestFullPruningDb : FullPruningDb
        {
            private readonly bool _successfulPruning;
            private readonly bool _clearPrunedDb;

            public TestPruningContext Context { get; set; } = null!;
            public new int PruningStarted { get; private set; }
            public ManualResetEvent WaitForClearDb { get; } = new(false);

            public TestFullPruningDb(RocksDbSettings settings, IRocksDbFactory dbFactory, bool successfulPruning, bool clearPrunedDb = false)
                : base(settings, dbFactory)
            {
                _successfulPruning = successfulPruning;
                _clearPrunedDb = clearPrunedDb;
            }

            protected override void ClearOldDb(IDb oldDb)
            {
                if (_clearPrunedDb)
                {
                    base.ClearOldDb(oldDb);
                    WaitForClearDb.Set();
                }
            }

            public override bool TryStartPruning(bool duplicateReads, out IPruningContext context)
            {
                if (base.TryStartPruning(duplicateReads, out context))
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
                    CancellationTokenSource.Dispose();
                }

                public byte[]? this[ReadOnlySpan<byte> key]
                {
                    get => _context[key];
                    set => _context[key] = value;
                }

                public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
                {
                    _context.Set(key, value, flags);
                }

                public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
                {
                    return _context.Get(key, flags);
                }

                public void Commit()
                {
                    WaitForFinish.Set();
                    if (_successfulPruning)
                    {
                        _context.Commit();
                    }
                }

                public void MarkStart()
                {
                    _context.MarkStart();
                }

                public CancellationTokenSource CancellationTokenSource { get; } = new();
            }
        }
    }
}
