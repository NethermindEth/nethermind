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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Common.Concurrency;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.StateSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;
using BlockTree = Nethermind.Blockchain.BlockTree;

namespace Nethermind.Synchronization.Test.FastSync
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class StateSyncFeedTests
    {
        private static IBlockTree _blockTree;
        private static IBlockTree BlockTree => LazyInitializer.EnsureInitialized(ref _blockTree, () => Build.A.BlockTree().OfChainLength(100).TestObject);
        
        private ILogger _logger;
        private ILogManager _logManager;

        private class SafeContext
        {
            public ISyncModeSelector SyncModeSelector;
            public ISyncPeerPool Pool;
            public StateSyncFeed Feed;
            public StateSyncDispatcher StateSyncDispatcher;   
        }

        [SetUp]
        public void Setup()
        {
            _logManager = NUnitLogManager.Instance;// LimboLogs.Instance;
            _logger = new NUnitLogger(LogLevel.Info);// LimboTraceLogger.Instance;
            TrieScenarios.InitOnce();
        }

        [TearDown]
        public void TearDown()
        {
            (_logger as ConsoleAsyncLogger)?.Flush();
        }

        private static StorageTree SetStorage(ITrieStore trieStore, byte i)
        {
            StorageTree remoteStorageTree = new StorageTree(trieStore, Keccak.EmptyTreeHash, LimboLogs.Instance);
            for (int j = 0; j < i; j++) remoteStorageTree.Set((UInt256) j, new[] {(byte) j, i});

            remoteStorageTree.Commit(0);
            return remoteStorageTree;
        }

        private class DbContext
        {
            private readonly ILogger _logger;

            public DbContext(ILogger logger, ILogManager logManager)
            {
                _logger = logger;
                RemoteDb = new MemDb();
                LocalDb = new MemDb();
                RemoteStateDb = RemoteDb;
                LocalStateDb = LocalDb;
                LocalCodeDb = new MemDb();
                RemoteCodeDb = new MemDb();
                RemoteTrieStore = new TrieStore(RemoteStateDb, logManager);

                RemoteStateTree = new StateTree(RemoteTrieStore, logManager);
                LocalStateTree = new StateTree(new TrieStore(LocalStateDb.Innermost, logManager), logManager);
            }

            public IDb RemoteCodeDb { get; }
            public IDb LocalCodeDb { get; }
            public MemDb RemoteDb { get; }
            public MemDb LocalDb { get; }
            public ITrieStore RemoteTrieStore { get; }
            public IDb RemoteStateDb { get; }
            public IDb LocalStateDb { get; }
            public StateTree RemoteStateTree { get; }
            public StateTree LocalStateTree { get; }

            public void CompareTrees(string stage, bool skipLogs = false)
            {
                if (!skipLogs) _logger.Info($"==================== {stage} ====================");
                LocalStateTree.RootHash = RemoteStateTree.RootHash;

                if (!skipLogs) _logger.Info("-------------------- REMOTE --------------------");
                TreeDumper dumper = new TreeDumper();
                RemoteStateTree.Accept(dumper, RemoteStateTree.RootHash);
                string remote = dumper.ToString();
                if (!skipLogs) _logger.Info(remote);
                if (!skipLogs) _logger.Info("-------------------- LOCAL --------------------");
                dumper.Reset();
                LocalStateTree.Accept(dumper, LocalStateTree.RootHash);
                string local = dumper.ToString();
                if (!skipLogs) _logger.Info(local);

                if (stage == "END")
                {
                    Assert.AreEqual(remote, local, $"{remote}{Environment.NewLine}{local}");
                    TrieStatsCollector collector = new TrieStatsCollector(LocalCodeDb, new OneLoggerLogManager(_logger));
                    LocalStateTree.Accept(collector, LocalStateTree.RootHash);
                    Assert.AreEqual(0, collector.Stats.MissingNodes);
                    Assert.AreEqual(0, collector.Stats.MissingCode);
                }

                //            Assert.AreEqual(dbContext._remoteCodeDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), dbContext._localCodeDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), "keys");
                //            Assert.AreEqual(dbContext._remoteCodeDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), dbContext._localCodeDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), "values");
                //
                //            Assert.AreEqual(dbContext._remoteDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), _localDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), "keys");
                //            Assert.AreEqual(dbContext._remoteDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), _localDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), "values");
            }

            public void CompareCodeDbs()
            {
                //            Assert.AreEqual(dbContext._remoteCodeDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), dbContext._localCodeDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), "keys");
                //            Assert.AreEqual(dbContext._remoteCodeDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), dbContext._localCodeDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), "values");

                //            Assert.AreEqual(dbContext._remoteDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), _localDb.Keys.OrderBy(k => k, Bytes.Comparer).ToArray(), "keys");
                //            Assert.AreEqual(dbContext._remoteDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), _localDb.Values.OrderBy(k => k, Bytes.Comparer).ToArray(), "values");
            }
        }

        private class SyncPeerMock : ISyncPeer
        {
            public static Func<IList<Keccak>, Task<byte[][]>> NotPreimage = request =>
            {
                var result = new byte[request.Count][];

                int i = 0;
                foreach (Keccak _ in request) result[i++] = new byte[] {1, 2, 3};

                return Task.FromResult(result);
            };

            public static Func<IList<Keccak>, Task<byte[][]>> EmptyArraysInResponses = request =>
            {
                var result = new byte[request.Count][];

                int i = 0;
                foreach (Keccak _ in request) result[i++] = new byte[0];

                return Task.FromResult(result);
            };

            private readonly IDb _codeDb;
            private readonly IDb _stateDb;

            private Func<IList<Keccak>, Task<byte[][]>> _executorResultFunction;

            private Keccak[] _filter;

            public SyncPeerMock(IDb stateDb, IDb codeDb, Func<IList<Keccak>, Task<byte[][]>> executorResultFunction = null)
            {
                _stateDb = stateDb;
                _codeDb = codeDb;

                if (executorResultFunction != null) _executorResultFunction = executorResultFunction;

                Node = new Node(TestItem.PublicKeyA, "127.0.0.1", 30302, true);
            }

            public int MaxResponseLength { get; set; } = int.MaxValue;

            public Node Node { get; }
            public string ClientId => "executorMock";
            public Keccak HeadHash { get; set; }
            public long HeadNumber { get; set; }
            public UInt256 TotalDifficulty { get; set; }
            public bool IsInitialized { get; set; }

            public void Disconnect(DisconnectReason reason, string details)
            {
                throw new NotImplementedException();
            }

            public Task<BlockBody[]> GetBlockBodies(IList<Keccak> blockHashes, CancellationToken token)
            {
                throw new NotImplementedException();
            }

            public Task<BlockHeader[]> GetBlockHeaders(Keccak blockHash, int maxBlocks, int skip, CancellationToken token)
            {
                throw new NotImplementedException();
            }

            public Task<BlockHeader[]> GetBlockHeaders(long number, int maxBlocks, int skip, CancellationToken token)
            {
                throw new NotImplementedException();
            }

            public Task<BlockHeader> GetHeadBlockHeader(Keccak hash, CancellationToken token)
            {
                return Task.FromResult(BlockTree.Head?.Header);
            }

            public void NotifyOfNewBlock(Block block, SendBlockPriority priority)
            {
                throw new NotImplementedException();
            }

            public PublicKey Id => Node.Id;

            public bool SendNewTransaction(Transaction transaction, bool isPriority)
            {
                throw new NotImplementedException();
            }

            public Task<TxReceipt[][]> GetReceipts(IList<Keccak> blockHash, CancellationToken token)
            {
                throw new NotImplementedException();
            }

            public Task<byte[][]> GetNodeData(IList<Keccak> hashes, CancellationToken token)
            {
                if (_executorResultFunction != null) return _executorResultFunction(hashes);

                var responses = new byte[hashes.Count][];

                int i = 0;
                foreach (Keccak item in hashes)
                {
                    if (i >= MaxResponseLength) break;

                    if (_filter == null || _filter.Contains(item)) responses[i] = _stateDb[item.Bytes] ?? _codeDb[item.Bytes];

                    i++;
                }

                return Task.FromResult(responses);
            }

            public void SetFilter(Keccak[] availableHashes)
            {
                _filter = availableHashes;
            }

            public void RegisterSatelliteProtocol<T>(string protocol, T protocolHandler) where T : class
            {
                throw new NotImplementedException();
            }

            public bool TryGetSatelliteProtocol<T>(string protocol, out T protocolHandler) where T : class
            {
                throw new NotImplementedException();
            }
        }

        private SafeContext PrepareDownloader(DbContext dbContext, ISyncPeer syncPeer)
        {
            SafeContext ctx = new SafeContext();
            ctx = new SafeContext();
            BlockTree blockTree = Build.A.BlockTree().OfChainLength((int) BlockTree.BestSuggestedHeader.Number).TestObject;
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            ctx.Pool = new SyncPeerPool(blockTree, new NodeStatsManager(timerFactory, LimboLogs.Instance), 25, LimboLogs.Instance);
            ctx.Pool.Start();
            ctx.Pool.AddPeer(syncPeer);

            SyncConfig syncConfig = new SyncConfig();
            syncConfig.FastSync = true;
            ctx.SyncModeSelector = StaticSelector.StateNodesWithFastBlocks;
            ctx.Feed = new StateSyncFeed(dbContext.LocalCodeDb, dbContext.LocalStateDb, new MemDb(), ctx.SyncModeSelector, blockTree, _logManager);
            ctx.StateSyncDispatcher =
                new StateSyncDispatcher(ctx.Feed, ctx.Pool, new StateSyncAllocationStrategyFactory(), _logManager);
            ctx.StateSyncDispatcher.Start(CancellationToken.None);
            return ctx;
        }

        private const int TimeoutLength = 1000;

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Retry(3)]
        public async Task Big_test((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            DbContext dbContext = new DbContext(_logger, _logManager);
            dbContext.RemoteCodeDb[Keccak.Compute(TrieScenarios.Code0).Bytes] = TrieScenarios.Code0;
            dbContext.RemoteCodeDb[Keccak.Compute(TrieScenarios.Code1).Bytes] = TrieScenarios.Code1;
            dbContext.RemoteCodeDb[Keccak.Compute(TrieScenarios.Code2).Bytes] = TrieScenarios.Code2;
            dbContext.RemoteCodeDb[Keccak.Compute(TrieScenarios.Code3).Bytes] = TrieScenarios.Code3;
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteTrieStore, dbContext.RemoteCodeDb);

            SyncPeerMock mock = new SyncPeerMock(dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            mock.SetFilter(((MemDb) dbContext.RemoteStateDb.Innermost).Keys.Take(((MemDb) dbContext.RemoteStateDb.Innermost).Keys.Count - 4).Select(k => new Keccak(k)).ToArray());

            dbContext.CompareTrees("BEFORE FIRST SYNC", true);

            SafeContext ctx = PrepareDownloader(dbContext, mock);
            await ActivateAndWait(ctx, dbContext, 1024);

            dbContext.CompareTrees("AFTER FIRST SYNC", true);

            dbContext.LocalStateTree.RootHash = dbContext.RemoteStateTree.RootHash;
            for (byte i = 0; i < 8; i++)
                dbContext.RemoteStateTree
                    .Set(TestItem.Addresses[i], TrieScenarios.AccountJustState0.WithChangedBalance(i)
                        .WithChangedNonce(1)
                        .WithChangedCodeHash(Keccak.Compute(TrieScenarios.Code3))
                        .WithChangedStorageRoot(SetStorage(dbContext.RemoteTrieStore, i).RootHash));

            dbContext.RemoteStateTree.UpdateRootHash();
            dbContext.RemoteStateTree.Commit(0);

            ctx.Feed.FallAsleep();
            ctx.Pool.WakeUpAll();

            await ActivateAndWait(ctx, dbContext, 1024);

            dbContext.CompareTrees("AFTER SECOND SYNC", true);

            dbContext.LocalStateTree.RootHash = dbContext.RemoteStateTree.RootHash;
            for (byte i = 0; i < 16; i++)
                dbContext.RemoteStateTree
                    .Set(TestItem.Addresses[i], TrieScenarios.AccountJustState0.WithChangedBalance(i)
                        .WithChangedNonce(2)
                        .WithChangedCodeHash(Keccak.Compute(TrieScenarios.Code3))
                        .WithChangedStorageRoot(SetStorage(dbContext.RemoteTrieStore, (byte) (i % 7)).RootHash));

            dbContext.RemoteStateTree.UpdateRootHash();
            dbContext.RemoteStateTree.Commit(0);
            
            ctx.Feed.FallAsleep();

            ctx.Pool.WakeUpAll();
            mock.SetFilter(null);
            await ActivateAndWait(ctx, dbContext, 1024);
            

            dbContext.CompareTrees("END");
            dbContext.CompareCodeDbs();
        }

        public static (string Name, Action<StateTree, ITrieStore, IDb> Action)[] Scenarios => TrieScenarios.Scenarios;

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        // [Retry(3)]
        public async Task Can_download_a_full_state((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            DbContext dbContext = new DbContext(_logger, _logManager);
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteTrieStore, dbContext.RemoteCodeDb);
            

            dbContext.CompareTrees("BEGIN");

            SyncPeerMock mock = new SyncPeerMock(dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            SafeContext ctx = PrepareDownloader(dbContext, mock);
            await ActivateAndWait(ctx, dbContext, 1024);
            
            dbContext.CompareTrees("END");
        }

        [Test]
        public async Task Can_download_an_empty_tree()
        {
            DbContext dbContext = new DbContext(_logger, _logManager);
            SyncPeerMock mock = new SyncPeerMock(dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            SafeContext ctx = PrepareDownloader(dbContext, mock);
            await ActivateAndWait(ctx, dbContext, 1000);
            dbContext.CompareTrees("END");
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Retry(3)]
        public async Task Can_download_in_multiple_connections((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            DbContext dbContext = new DbContext(_logger, _logManager);
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteTrieStore, dbContext.RemoteCodeDb);
            

            SyncPeerMock mock = new SyncPeerMock(dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            mock.SetFilter(new[] {dbContext.RemoteStateTree.RootHash});

            SafeContext ctx = PrepareDownloader(dbContext, mock);
            await ActivateAndWait(ctx, dbContext, 1024, 1000);
            

            ctx.Pool.WakeUpAll();
            mock.SetFilter(null);
            ctx.Feed.FallAsleep();
            await ActivateAndWait(ctx, dbContext, 1024);
            

            dbContext.CompareTrees("END");
        }

        private async Task ActivateAndWait(SafeContext safeContext, DbContext dbContext, long blockNumber, int timeout = TimeoutLength)
        {
            DotNetty.Common.Concurrency.TaskCompletionSource dormantAgainSource = new DotNetty.Common.Concurrency.TaskCompletionSource();
            safeContext.Feed.StateChanged += (s, e) =>
            {
                if (e.NewState == SyncFeedState.Dormant)
                {
                    dormantAgainSource.TrySetResult(0);
                }
            };

            safeContext.Feed.ResetStateRoot(blockNumber, dbContext.RemoteStateTree.RootHash);
            safeContext.Feed.Activate();
            var watch = Stopwatch.StartNew();
            await Task.WhenAny(
                dormantAgainSource.Task,
                Task.Delay(timeout));
            TestContext.WriteLine($"Waited {watch.ElapsedMilliseconds}");
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        // [Retry(3)]
        public async Task Can_download_when_executor_sends_shorter_responses((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            DbContext dbContext = new DbContext(_logger, _logManager);
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteTrieStore, dbContext.RemoteCodeDb);
            

            dbContext.CompareTrees("BEGIN");

            SyncPeerMock mock = new SyncPeerMock(dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            mock.MaxResponseLength = 1;

            SafeContext ctx = PrepareDownloader(dbContext, mock);
            await ActivateAndWait(ctx, dbContext, 1024);
            

            dbContext.CompareTrees("END");
        }

        [Test]
        [Retry(3)]
        public async Task When_saving_root_goes_asleep()
        {
            DbContext dbContext = new DbContext(_logger, _logManager);
            dbContext.RemoteStateTree.Set(TestItem.KeccakA, Build.An.Account.TestObject);
            dbContext.RemoteStateTree.Commit(0);
            

            dbContext.CompareTrees("BEGIN");

            SyncPeerMock mock = new SyncPeerMock(dbContext.RemoteStateDb, dbContext.RemoteCodeDb);

            SafeContext ctx = PrepareDownloader(dbContext, mock);
            await ActivateAndWait(ctx, dbContext, 1024);
            
            dbContext.CompareTrees("END");

            ctx.Feed.CurrentState.Should().Be(SyncFeedState.Dormant);
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Retry(3)]
        public async Task Can_download_with_moving_target((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            DbContext dbContext = new DbContext(_logger, _logManager);
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteTrieStore, dbContext.RemoteCodeDb);
            

            SyncPeerMock mock = new SyncPeerMock(dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            mock.SetFilter(((MemDb) dbContext.RemoteStateDb.Innermost).Keys.Take(((MemDb) dbContext.RemoteStateDb.Innermost).Keys.Count - 1).Select(k => new Keccak(k)).ToArray());

            dbContext.CompareTrees("BEFORE FIRST SYNC");

            SafeContext ctx = PrepareDownloader(dbContext, mock);
            await ActivateAndWait(ctx, dbContext, 1024, 1000);
            

            dbContext.CompareTrees("AFTER FIRST SYNC");

            dbContext.LocalStateTree.RootHash = dbContext.RemoteStateTree.RootHash;
            dbContext.RemoteStateTree.Set(TestItem.AddressA, TrieScenarios.AccountJustState0.WithChangedBalance(123.Ether()));
            dbContext.RemoteStateTree.Set(TestItem.AddressB, TrieScenarios.AccountJustState1.WithChangedBalance(123.Ether()));
            dbContext.RemoteStateTree.Set(TestItem.AddressC, TrieScenarios.AccountJustState2.WithChangedBalance(123.Ether()));

            dbContext.CompareTrees("BEFORE ROOT HASH UPDATE");

            dbContext.RemoteStateTree.UpdateRootHash();

            dbContext.CompareTrees("BEFORE COMMIT");

            dbContext.RemoteStateTree.Commit(1);
            

            ctx.Pool.WakeUpAll();

            ctx.Feed.FallAsleep();
            mock.SetFilter(null);
            await ActivateAndWait(ctx, dbContext, 1024, 2000);
            

            dbContext.CompareTrees("END");
            dbContext.CompareCodeDbs();
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Retry(3)]
        public async Task Dependent_branch_counter_is_zero_and_leaf_is_short((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            DbContext dbContext = new DbContext(_logger, _logManager);
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteTrieStore, dbContext.RemoteCodeDb);
            

            StorageTree remoteStorageTree = new StorageTree(dbContext.RemoteTrieStore, Keccak.EmptyTreeHash, LimboLogs.Instance);
            remoteStorageTree.Set(
                Bytes.FromHexString("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb000"), new byte[] {1});
            remoteStorageTree.Set(
                Bytes.FromHexString("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb111"), new byte[] {1});
            remoteStorageTree.Set(
                Bytes.FromHexString("eeeeeeeeeeeeeeeeeeeeeb000000000000000000000000000000000000000000"), new byte[] {1});
            remoteStorageTree.Set(
                Bytes.FromHexString("eeeeeeeeeeeeeeeeeeeeeb111111111111111111111111111111111111111111"), new byte[] {1});
            remoteStorageTree.Commit(0);

            dbContext.RemoteStateTree.Set(TestItem.AddressD, TrieScenarios.AccountJustState0.WithChangedStorageRoot(remoteStorageTree.RootHash));
            dbContext.RemoteStateTree.Commit(0);

            dbContext.CompareTrees("BEGIN");

            SyncPeerMock mock = new SyncPeerMock(dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            SafeContext ctx = PrepareDownloader(dbContext, mock);
            await ActivateAndWait(ctx, dbContext, 1024);
            

            dbContext.CompareTrees("END");
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Retry(3)]
        public async Task Scenario_plus_one_code((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            DbContext dbContext = new DbContext(_logger, _logManager);
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteTrieStore, dbContext.RemoteCodeDb);
            

            dbContext.RemoteCodeDb.Set(Keccak.Compute(TrieScenarios.Code0), TrieScenarios.Code0);

            var changedAccount = TrieScenarios.AccountJustState0.WithChangedCodeHash(Keccak.Compute(TrieScenarios.Code0));
            dbContext.RemoteStateTree.Set(TestItem.AddressD, changedAccount);
            dbContext.RemoteStateTree.Commit(0);

            dbContext.CompareTrees("BEGIN");

            SyncPeerMock mock = new SyncPeerMock(dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            SafeContext ctx = PrepareDownloader(dbContext, mock);
            await ActivateAndWait(ctx, dbContext, 1024);
            

            dbContext.CompareTrees("END");
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Retry(3)]
        public async Task Scenario_plus_one_code_one_storage((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            DbContext dbContext = new DbContext(_logger, _logManager);
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteTrieStore, dbContext.RemoteCodeDb);
            

            dbContext.RemoteCodeDb.Set(Keccak.Compute(TrieScenarios.Code0), TrieScenarios.Code0);

            StorageTree remoteStorageTree = new StorageTree(dbContext.RemoteTrieStore, Keccak.EmptyTreeHash, _logManager);
            remoteStorageTree.Set((UInt256) 1, new byte[] {1});
            remoteStorageTree.Commit(0);

            dbContext.RemoteStateTree.Set(TestItem.AddressD, TrieScenarios.AccountJustState0.WithChangedCodeHash(Keccak.Compute(TrieScenarios.Code0)).WithChangedStorageRoot(remoteStorageTree.RootHash));
            dbContext.RemoteStateTree.Commit(0);

            dbContext.CompareTrees("BEGIN");

            SyncPeerMock mock = new SyncPeerMock(dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            SafeContext ctx = PrepareDownloader(dbContext, mock);
            await ActivateAndWait(ctx, dbContext, 1024);
            

            dbContext.CompareTrees("END");
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        [Retry(3)]
        public async Task Scenario_plus_one_storage((string Name, Action<StateTree, ITrieStore, IDb> SetupTree) testCase)
        {
            DbContext dbContext = new DbContext(_logger, _logManager);
            testCase.SetupTree(dbContext.RemoteStateTree, dbContext.RemoteTrieStore, dbContext.RemoteCodeDb);
            

            StorageTree remoteStorageTree = new StorageTree(dbContext.RemoteTrieStore, Keccak.EmptyTreeHash, _logManager);
            remoteStorageTree.Set((UInt256) 1, new byte[] {1});
            remoteStorageTree.Commit(0);

            dbContext.RemoteStateTree.Set(TestItem.AddressD, TrieScenarios.AccountJustState0.WithChangedStorageRoot(remoteStorageTree.RootHash));
            dbContext.RemoteStateTree.Set(TestItem.AddressD, TrieScenarios.AccountJustState0.WithChangedStorageRoot(remoteStorageTree.RootHash));
            dbContext.RemoteStateTree.Commit(0);

            dbContext.CompareTrees("BEGIN");

            SyncPeerMock mock = new SyncPeerMock(dbContext.RemoteStateDb, dbContext.RemoteCodeDb);
            SafeContext ctx = PrepareDownloader(dbContext, mock);
            await ActivateAndWait(ctx, dbContext, 1024);
            

            dbContext.CompareTrees("END");
        }

        // [Test, Retry(5)]
        // public async Task Silences_bad_peers()
        // {
        //     DbContext dbContext = new DbContext(_logger, _logManager);
        //     SyncPeerMock mock = new SyncPeerMock(dbContext.RemoteStateDb, dbContext.RemoteCodeDb, SyncPeerMock.NotPreimage);
        //     SafeContext ctx = PrepareDownloader(mock);
        //     _feed.SetNewStateRoot(1024, Keccak.Compute("the_peer_has_no_data"));
        //     _feed.Activate();
        //     await Task.WhenAny(_stateSyncDispatcher.Start(CancellationToken.None), Task.Delay(1000)).Unwrap()
        //         .ContinueWith(t =>
        //         {
        //             Assert.AreEqual(0, _pool.InitializedPeers.Count(p => p.CanBeAllocated(AllocationContexts.All)));
        //         });
        // }

        // [Test]
        // [Retry(3)]
        // public async Task Silences_when_peer_sends_empty_byte_arrays()
        // {
        //     DbContext dbContext = new DbContext(_logger, _logManager);
        //     SyncPeerMock mock = new SyncPeerMock(dbContext.RemoteStateDb, dbContext.RemoteCodeDb, SyncPeerMock.EmptyArraysInResponses);
        //     SafeContext ctx = PrepareDownloader(mock);
        //     _feed.SetNewStateRoot(1024, Keccak.Compute("the_peer_has_no_data"));
        //     _feed.Activate();
        //     await Task.WhenAny(_stateSyncDispatcher.Start(CancellationToken.None), Task.Delay(1000)).Unwrap()
        //         .ContinueWith(t =>
        //         {
        //             _pool.InitializedPeers.Count(p => p.CanBeAllocated(AllocationContexts.All)).Should().Be(0);
        //         });
        // }
    }
}
