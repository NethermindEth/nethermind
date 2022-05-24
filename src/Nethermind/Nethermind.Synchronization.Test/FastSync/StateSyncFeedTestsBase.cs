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
    public class StateSyncFeedTestsBase
    {
        private const int TimeoutLength = 1000;

        protected static IBlockTree _blockTree;
        private static IBlockTree BlockTree => LazyInitializer.EnsureInitialized(ref _blockTree, () => Build.A.BlockTree().OfChainLength(100).TestObject);
        
        protected ILogger _logger;
        protected ILogManager _logManager;

        public static (string Name, Action<StateTree, ITrieStore, IDb> Action)[] Scenarios => TrieScenarios.Scenarios;

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

        protected static StorageTree SetStorage(ITrieStore trieStore, byte i)
        {
            StorageTree remoteStorageTree = new StorageTree(trieStore, Keccak.EmptyTreeHash, LimboLogs.Instance);
            for (int j = 0; j < i; j++) remoteStorageTree.Set((UInt256) j, new[] {(byte) j, i});

            remoteStorageTree.Commit(0);
            return remoteStorageTree;
        }

        protected SafeContext PrepareDownloader(DbContext dbContext, ISyncPeer syncPeer)
        {
            SafeContext ctx = new SafeContext();
            ctx = new SafeContext();
            BlockTree blockTree = Build.A.BlockTree().OfChainLength((int)BlockTree.BestSuggestedHeader.Number).TestObject;
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            ctx.Pool = new SyncPeerPool(blockTree, new NodeStatsManager(timerFactory, LimboLogs.Instance), new TotalDifficultyBasedBetterPeerStrategy(null, LimboLogs.Instance), 25, LimboLogs.Instance);
            ctx.Pool.Start();
            ctx.Pool.AddPeer(syncPeer);

            SyncConfig syncConfig = new SyncConfig();
            syncConfig.FastSync = true;
            ctx.SyncModeSelector = StaticSelector.StateNodesWithFastBlocks;
            ctx.TreeFeed = new(SyncMode.StateNodes, dbContext.LocalCodeDb, dbContext.LocalStateDb, blockTree, _logManager);
            ctx.Feed = new StateSyncFeed(ctx.SyncModeSelector, ctx.TreeFeed, _logManager);
            ctx.StateSyncDispatcher =
                new StateSyncDispatcher(ctx.Feed, ctx.Pool, new StateSyncAllocationStrategyFactory(), _logManager);
            ctx.StateSyncDispatcher.Start(CancellationToken.None);
            return ctx;
        }

        protected async Task ActivateAndWait(SafeContext safeContext, DbContext dbContext, long blockNumber, int timeout = TimeoutLength)
        {
            DotNetty.Common.Concurrency.TaskCompletionSource dormantAgainSource = new DotNetty.Common.Concurrency.TaskCompletionSource();
            safeContext.Feed.StateChanged += (s, e) =>
            {
                if (e.NewState == SyncFeedState.Dormant)
                {
                    dormantAgainSource.TrySetResult(0);
                }
            };

            safeContext.TreeFeed.ResetStateRoot(blockNumber, dbContext.RemoteStateTree.RootHash, safeContext.Feed.CurrentState);
            safeContext.Feed.Activate();
            var watch = Stopwatch.StartNew();
            await Task.WhenAny(
                dormantAgainSource.Task,
                Task.Delay(timeout));
            TestContext.WriteLine($"Waited {watch.ElapsedMilliseconds}");
        }

        protected class SafeContext
        {
            public ISyncModeSelector SyncModeSelector;
            public ISyncPeerPool Pool;
            public TreeSync TreeFeed;
            public StateSyncFeed Feed;
            public StateSyncDispatcher StateSyncDispatcher;
        }

        protected class DbContext
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
                LocalStateTree = new StateTree(new TrieStore(LocalStateDb, logManager), logManager);
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

        protected class SyncPeerMock : ISyncPeer
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

            private Func<IReadOnlyList<Keccak>, Task<byte[][]>> _executorResultFunction;

            private Keccak[] _filter;

            public SyncPeerMock(IDb stateDb, IDb codeDb, Func<IReadOnlyList<Keccak>, Task<byte[][]>> executorResultFunction = null)
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
            public bool IsPriority { get; set; }

            public void Disconnect(DisconnectReason reason, string details)
            {
                throw new NotImplementedException();
            }

            public Task<BlockBody[]> GetBlockBodies(IReadOnlyList<Keccak> blockHashes, CancellationToken token)
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

            public void SendNewTransactions(IEnumerable<Transaction> txs, bool sendFullTx)
            {
                throw new NotImplementedException();
            }

            public Task<TxReceipt[][]> GetReceipts(IReadOnlyList<Keccak> blockHash, CancellationToken token)
            {
                throw new NotImplementedException();
            }

            public Task<byte[][]> GetNodeData(IReadOnlyList<Keccak> hashes, CancellationToken token)
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
    }
}
