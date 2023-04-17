// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
        protected readonly TrieNodeResolverCapability _resolverCapability;

        private const int TimeoutLength = 2000;

        protected static IBlockTree _blockTree;
        protected static IBlockTree BlockTree => LazyInitializer.EnsureInitialized(ref _blockTree, () => Build.A.BlockTree().OfChainLength(100).TestObject);

        protected ILogger _logger;
        protected ILogManager _logManager;

        private readonly int _defaultPeerCount;
        private readonly int _defaultPeerMaxRandomLatency;

        public StateSyncFeedTestsBase(TrieNodeResolverCapability capability, int defaultPeerCount = 1, int defaultPeerMaxRandomLatency = 0)
        {
            _resolverCapability = capability;
            _defaultPeerCount = defaultPeerCount;
            _defaultPeerMaxRandomLatency = defaultPeerMaxRandomLatency;
        }

        public static (string Name, Action<StateTree, ITrieStore, IDb> Action)[] Scenarios => TrieScenarios.Scenarios;

        [SetUp]
        public void Setup()
        {
            _logManager = new NUnitLogManager(LogLevel.Trace);
            _logger = _logManager.GetClassLogger();
            TrieScenarios.InitOnce();
        }

        [TearDown]
        public void TearDown()
        {
            (_logger as ConsoleAsyncLogger)?.Flush();
        }

        protected static StorageTree SetStorage(ITrieStore trieStore, byte i, Address address)
        {
            StorageTree remoteStorageTree = new StorageTree(trieStore, Keccak.EmptyTreeHash, LimboLogs.Instance, address);
            for (int j = 0; j < i; j++) remoteStorageTree.Set((UInt256)j, new[] { (byte)j, i });

            remoteStorageTree.Commit(0);
            return remoteStorageTree;
        }

        protected SafeContext PrepareDownloader(DbContext dbContext, Action<SyncPeerMock>? mockMutator = null)
        {
            SyncPeerMock[] syncPeers = new SyncPeerMock[_defaultPeerCount];
            for (int i = 0; i < _defaultPeerCount; i++)
            {
                Node node = new Node(TestItem.PublicKeys[i], $"127.0.0.{i}", 30302, true)
                {
                    EthDetails = "eth66",
                };
                SyncPeerMock mock = new SyncPeerMock(dbContext.RemoteStateDb, dbContext.RemoteCodeDb, node: node, maxRandomizedLatencyMs: _defaultPeerMaxRandomLatency);
                mockMutator?.Invoke(mock);
                syncPeers[i] = mock;
            }

            SafeContext ctx = PrepareDownloaderWithPeer(dbContext, syncPeers);
            ctx.SyncPeerMocks = syncPeers;
            return ctx;
        }

        protected SafeContext PrepareDownloaderWithPeer(DbContext dbContext, params ISyncPeer[] syncPeers)
        {
            SafeContext ctx = new SafeContext();
            ctx = new SafeContext();
            BlockTree blockTree = Build.A.BlockTree().OfChainLength((int)BlockTree.BestSuggestedHeader.Number).TestObject;
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            ctx.Pool = new SyncPeerPool(blockTree, new NodeStatsManager(timerFactory, LimboLogs.Instance), new TotalDifficultyBetterPeerStrategy(LimboLogs.Instance), LimboLogs.Instance, 25);
            ctx.Pool.Start();

            for (int i = 0; i < syncPeers.Length; i++)
            {
                ctx.Pool.AddPeer(syncPeers[i]);
            }

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
        }

        protected class SafeContext
        {
            public ISyncModeSelector SyncModeSelector;
            public SyncPeerMock[] SyncPeerMocks;
            public ISyncPeerPool Pool;
            public TreeSync TreeFeed;
            public StateSyncFeed Feed;
            public StateSyncDispatcher StateSyncDispatcher;
        }

        protected class DbContext
        {
            private readonly ILogger _logger;

            public DbContext(TrieNodeResolverCapability capability, ILogger logger, ILogManager logManager)
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

                ITrieStore localTrieStore = capability.CreateTrieStore(LocalStateDb, logManager);
                LocalStateTree = capability switch
                {
                    TrieNodeResolverCapability.Hash => new StateTree(localTrieStore, logManager),
                    TrieNodeResolverCapability.Path => new StateTreeByPath(localTrieStore, logManager),
                    _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null)
                };
            }

            public IDb RemoteCodeDb { get; }
            public IDb LocalCodeDb { get; }
            public MemDb RemoteDb { get; }
            public MemDb LocalDb { get; }
            public ITrieStore RemoteTrieStore { get; }
            public IDb RemoteStateDb { get; }
            public IDb LocalStateDb { get; }
            public StateTree RemoteStateTree { get; }
            public IStateTree LocalStateTree { get; }

            public void CompareTrees(string stage, bool skipLogs = true)
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
                    TrieStatsCollector collector = new(LocalCodeDb, LimboLogs.Instance);
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
                foreach (Keccak _ in request) result[i++] = new byte[] { 1, 2, 3 };

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

            private readonly long _maxRandomizedLatencyMs;

            public SyncPeerMock(
                IDb stateDb,
                IDb codeDb,
                Func<IReadOnlyList<Keccak>, Task<byte[][]>> executorResultFunction = null,
                long? maxRandomizedLatencyMs = null,
                Node? node = null
            )
            {
                _stateDb = stateDb;
                _codeDb = codeDb;

                if (executorResultFunction is not null) _executorResultFunction = executorResultFunction;

                Node = node ?? new Node(TestItem.PublicKeyA, "127.0.0.1", 30302, true) { EthDetails = "eth66" };
                _maxRandomizedLatencyMs = maxRandomizedLatencyMs ?? 0;
            }

            public int MaxResponseLength { get; set; } = int.MaxValue;

            public Node Node { get; }
            public string ClientId => "executorMock";
            public Keccak HeadHash { get; set; }
            public long HeadNumber { get; set; }
            public UInt256 TotalDifficulty { get; set; }
            public bool IsInitialized { get; set; }
            public bool IsPriority { get; set; }
            public byte ProtocolVersion { get; }
            public string ProtocolCode { get; }

            public void Disconnect(InitiateDisconnectReason reason, string details)
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

            public Task<BlockHeader?> GetHeadBlockHeader(Keccak? hash, CancellationToken token)
            {
                return Task.FromResult(BlockTree.Head?.Header);
            }

            public void NotifyOfNewBlock(Block block, SendBlockMode mode)
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

            public async Task<byte[][]> GetNodeData(IReadOnlyList<Keccak> hashes, CancellationToken token)
            {
                if (_maxRandomizedLatencyMs != 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(TestContext.CurrentContext.Random.NextLong() % _maxRandomizedLatencyMs));
                }

                if (_executorResultFunction is not null) return await _executorResultFunction(hashes);

                var responses = new byte[hashes.Count][];

                int i = 0;
                foreach (Keccak item in hashes)
                {
                    if (i >= MaxResponseLength) break;

                    if (_filter is null || _filter.Contains(item)) responses[i] = _stateDb[item.Bytes] ?? _codeDb[item.Bytes];

                    i++;
                }

                return responses;
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
                protocolHandler = null;
                return false;
            }
        }
    }
}
