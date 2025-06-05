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

namespace Nethermind.Blockchain.Test.FullPruning;


[TestFixture(0, 1)]
[TestFixture(0, 4)]
[TestFixture(1, 1)]
[TestFixture(1, 4)]
[Parallelizable(ParallelScope.Children)]
public class FullPrunerTests
{
    private readonly int _fullPrunerMemoryBudgetMb;
    private readonly int _degreeOfParallelism;

    public FullPrunerTests(int fullPrunerMemoryBudgetMb, int degreeOfParallelism)
    {
        _fullPrunerMemoryBudgetMb = fullPrunerMemoryBudgetMb;
        _degreeOfParallelism = degreeOfParallelism;
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task can_prune()
    {
        TestContext test = CreateTest();
        await test.RunFullPruning();
        test.ShouldCopyAllValues();
    }

    [MaxTime(Timeout.MaxTestTime * 2)] // this is particular long test
    [TestCase(INodeStorage.KeyScheme.Hash, INodeStorage.KeyScheme.Current, INodeStorage.KeyScheme.Hash)]
    [TestCase(INodeStorage.KeyScheme.HalfPath, INodeStorage.KeyScheme.Current, INodeStorage.KeyScheme.HalfPath)]
    [TestCase(INodeStorage.KeyScheme.Hash, INodeStorage.KeyScheme.HalfPath, INodeStorage.KeyScheme.HalfPath)]
    [TestCase(INodeStorage.KeyScheme.HalfPath, INodeStorage.KeyScheme.Hash, INodeStorage.KeyScheme.HalfPath)]
    public async Task can_prune_and_switch_key_scheme(INodeStorage.KeyScheme currentKeyScheme, INodeStorage.KeyScheme newKeyScheme, INodeStorage.KeyScheme expectedNewScheme)
    {
        TestContext test = new(
            true,
            false,
            FullPruningCompletionBehavior.None,
            _fullPrunerMemoryBudgetMb,
            _degreeOfParallelism,
            currentKeyScheme: currentKeyScheme,
            preferredKeyScheme: newKeyScheme);

        test.NodeStorage.Scheme.Should().Be(currentKeyScheme);
        await test.RunFullPruning();
        test.ShouldCopyAllValuesWhenVisitingTrie();
        test.NodeStorage.Scheme.Should().Be(expectedNewScheme);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task pruning_deletes_old_db_on_success()
    {
        TestContext test = CreateTest(clearPrunedDb: true);
        await test.RunFullPruning();
        test.TrieDb.Count.Should().Be(0);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task pruning_keeps_old_db_on_fail()
    {
        TestContext test = CreateTest(false);
        int count = test.TrieDb.Count;
        await test.RunFullPruning();
        test.TrieDb.Count.Should().Be(count);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task pruning_deletes_new_db_on_fail()
    {
        TestContext test = CreateTest(false);
        await test.RunFullPruning();
        test.CopyDb.Count.Should().Be(0);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task pruning_keeps_new_db_on_success()
    {
        TestContext test = CreateTest();
        int count = test.TrieDb.Count;
        await test.RunFullPruning();
        test.CopyDb.Count.Should().Be(count);
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(true, FullPruningCompletionBehavior.None, false)]
    [TestCase(true, FullPruningCompletionBehavior.ShutdownOnSuccess, true)]
    [TestCase(true, FullPruningCompletionBehavior.AlwaysShutdown, true)]
    [TestCase(false, FullPruningCompletionBehavior.None, false)]
    [TestCase(false, FullPruningCompletionBehavior.ShutdownOnSuccess, false)]
    [TestCase(false, FullPruningCompletionBehavior.AlwaysShutdown, true)]
    [Retry(10)]
    public async Task pruning_shuts_down_node(bool success, FullPruningCompletionBehavior behavior, bool expectedShutdown)
    {
        TestContext test = CreateTest(successfulPruning: success, completionBehavior: behavior);
        await test.RunFullPruning();

        if (expectedShutdown)
        {
            test.ProcessExitSource.Received(1).Exit(ExitCodes.Ok);
        }
        else
        {
            test.ProcessExitSource.DidNotReceiveWithAnyArgs().Exit(ExitCodes.Ok);
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task can_not_start_pruning_when_other_is_in_progress()
    {
        TestContext test = CreateTest();
        test.FullPruningDb.CanStartPruning.Should().BeTrue();

        test.TriggerPruningViaEvent();
        TestFullPruningDb.TestPruningContext pruningContext = await test.WaitForPruningStart();
        test.FullPruningDb.CanStartPruning.Should().BeFalse();
        await test.WaitForPruningEnd(pruningContext);

        test.FullPruningDb.CanStartPruning.Should().BeTrue();
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task should_not_start_multiple_pruning()
    {
        TestContext test = CreateTest();
        test.TriggerPruningViaEvent();
        TestFullPruningDb.TestPruningContext ctx = await test.WaitForPruningStart();
        test.TriggerPruningViaEvent();
        await test.WaitForPruningEnd(ctx);
        test.FullPruningDb.PruningStarted.Should().Be(1);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task should_duplicate_writes_while_pruning()
    {
        TestContext test = CreateTest();
        TestFullPruningDb.TestPruningContext ctx = await test.WaitForPruningStart();
        byte[] key = { 1, 2, 3 };
        test.FullPruningDb[key] = key;
        test.FullPruningDb.Context.WaitForFinish.Set();

        await test.WaitForPruningEnd(ctx);
        test.FullPruningDb[key].Should().BeEquivalentTo(key);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task should_duplicate_writes_to_batches_while_pruning()
    {
        TestContext test = CreateTest();
        byte[] key = { 0, 1, 2 };
        TestFullPruningDb.TestPruningContext context = await test.WaitForPruningStart();

        using (IWriteBatch writeBatch = test.FullPruningDb.StartWriteBatch())
        {
            writeBatch[key] = key;
        }

        await test.WaitForPruningEnd(context);

        test.FullPruningDb[key].Should().BeEquivalentTo(key);
    }

    private TestContext CreateTest(
        bool successfulPruning = true,
        bool clearPrunedDb = false,
        FullPruningCompletionBehavior completionBehavior = FullPruningCompletionBehavior.None) =>
        new(
            successfulPruning,
            clearPrunedDb,
            completionBehavior,
            _fullPrunerMemoryBudgetMb,
            _degreeOfParallelism);

    private class TestContext
    {
        private readonly bool _clearPrunedDb;
        private readonly Hash256 _stateRoot;
        private long _head;
        public TestFullPruningDb FullPruningDb { get; }
        public IPruningTrigger PruningTrigger { get; } = Substitute.For<IPruningTrigger>();
        public IBlockTree BlockTree { get; } = Substitute.For<IBlockTree>();
        public IStateReader StateReader { get; }
        public FullPruner Pruner { get; }
        public MemDb TrieDb { get; }
        public INodeStorage NodeStorage { get; }
        public TestMemDb CopyDb { get; }
        public IDriveInfo DriveInfo { get; set; } = Substitute.For<IDriveInfo>();
        public IChainEstimations _chainEstimations = ChainSizes.UnknownChain.Instance;

        public IProcessExitSource ProcessExitSource { get; } = Substitute.For<IProcessExitSource>();

        public TestContext(
            bool successfulPruning,
            bool clearPrunedDb = false,
            FullPruningCompletionBehavior completionBehavior = FullPruningCompletionBehavior.None,
            int fullScanMemoryBudgetMb = 0,
            int degreeOfParallelism = 0,
            INodeStorage.KeyScheme currentKeyScheme = INodeStorage.KeyScheme.HalfPath,
            INodeStorage.KeyScheme preferredKeyScheme = INodeStorage.KeyScheme.Current)
        {
            BlockTree.OnUpdateMainChain += (_, e) => _head = e.Blocks[^1].Number;
            _clearPrunedDb = clearPrunedDb;
            TrieDb = new TestMemDb();
            CopyDb = new TestMemDb();
            IDbFactory dbFactory = Substitute.For<IDbFactory>();
            dbFactory.CreateDb(Arg.Any<DbSettings>()).Returns(TrieDb, CopyDb);

            NodeStorage storageForWrite = new NodeStorage(TrieDb, currentKeyScheme);
            PatriciaTree trie = Build.A.Trie(storageForWrite).WithAccountsByIndex(0, 100).TestObject;
            _stateRoot = trie.RootHash;
            FullPruningDb = new TestFullPruningDb(new DbSettings("test", "test"), dbFactory, successfulPruning, clearPrunedDb);
            NodeStorageFactory nodeStorageFactory = new NodeStorageFactory(preferredKeyScheme, LimboLogs.Instance);
            nodeStorageFactory.DetectCurrentKeySchemeFrom(TrieDb);
            NodeStorage = nodeStorageFactory.WrapKeyValueStore(FullPruningDb);

            var trieStore = TestTrieStoreFactory.Build(NodeStorage, LimboLogs.Instance);
            StateReader = new StateReader(trieStore, new TestMemDb(), LimboLogs.Instance);

            Pruner = new(
                FullPruningDb,
                nodeStorageFactory,
                NodeStorage,
                PruningTrigger,
                new PruningConfig()
                {
                    FullPruningMaxDegreeOfParallelism = degreeOfParallelism,
                    FullPruningMemoryBudgetMb = fullScanMemoryBudgetMb,
                    FullPruningCompletionBehavior = completionBehavior
                },
                BlockTree,
                StateReader,
                ProcessExitSource,
                _chainEstimations,
                DriveInfo,
                trieStore,
                LimboLogs.Instance);
        }

        public async Task RunFullPruning()
        {
            TestFullPruningDb.TestPruningContext ctx = await WaitForPruningStart();
            await WaitForPruningEnd(ctx);
        }

        public void TriggerPruningViaEvent()
        {
            PruningTrigger.Prune += Raise.Event<EventHandler<PruningTriggerEventArgs>>();
        }

        public async Task<bool> WaitForPruningEnd(TestFullPruningDb.TestPruningContext context)
        {
            while (!await context.WaitForFinish.WaitOneAsync(TimeSpan.FromMilliseconds(1), CancellationToken.None))
            {
                AddBlocks(1);
            }
            AddBlocks(1);
            return await context.DisposeEvent.WaitOneAsync(TimeSpan.FromMilliseconds(Timeout.MaxWaitTime * 5), CancellationToken.None);
        }

        public async Task<TestFullPruningDb.TestPruningContext> WaitForPruningStart()
        {
            TriggerPruningViaEvent();
            using CancellationTokenSource cts = new CancellationTokenSource();
            Task addBlockTasks = Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    AddBlocks(1);
                }
            });

            try
            {
                Assert.That(() => FullPruningDb.Context, Is.Not.Null.After(Timeout.MaxTestTime, 1));
            }
            finally
            {
                await cts.CancelAsync();
                await addBlockTasks;
            }

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
                Thread.Sleep(1); // Need to add a little sleep as the wait for event in full pruner is async.
            }
        }

        public void ShouldCopyAllValuesWhenVisitingTrie()
        {
            PatriciaTree trie = new PatriciaTree(new RawScopedTrieStore(new NodeStorage(TrieDb)), LimboLogs.Instance);
            TrieCopiedNodeVisitor visitor = new TrieCopiedNodeVisitor(new NodeStorage(CopyDb));
            trie.Accept(visitor, BlockTree.Head!.StateRoot!);
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

        public TestFullPruningDb(DbSettings settings, IDbFactory dbFactory, bool successfulPruning, bool clearPrunedDb = false)
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

            public IWriteBatch StartWriteBatch()
            {
                return _context.StartWriteBatch();
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

    class TrieCopiedNodeVisitor : ITreeVisitor<TreePathContextWithStorage>
    {
        private readonly INodeStorage _nodeStorageToCompareTo;

        public TrieCopiedNodeVisitor(INodeStorage nodeStorage)
        {
            _nodeStorageToCompareTo = nodeStorage;
        }

        private void CheckNode(Hash256? storage, in TreePath path, TrieNode node)
        {
            _nodeStorageToCompareTo.KeyExists(storage, path, node.Keccak).Should().BeTrue();
        }

        public bool IsFullDbScan => true;
        public bool ShouldVisit(in TreePathContextWithStorage ctx, in ValueHash256 nextNode) => true;

        public void VisitTree(in TreePathContextWithStorage ctx, in ValueHash256 rootHash)
        {
        }

        public void VisitMissingNode(in TreePathContextWithStorage ctx, in ValueHash256 nodeHash)
        {
        }

        public void VisitBranch(in TreePathContextWithStorage ctx, TrieNode node)
        {
            CheckNode(ctx.Storage, ctx.Path, node);
        }

        public void VisitExtension(in TreePathContextWithStorage ctx, TrieNode node)
        {
            CheckNode(ctx.Storage, ctx.Path, node);
        }

        public void VisitLeaf(in TreePathContextWithStorage ctx, TrieNode node)
        {
            CheckNode(ctx.Storage, ctx.Path, node);
        }

        public void VisitAccount(in TreePathContextWithStorage ctx, TrieNode node, in AccountStruct account)
        {
        }
    }
}
