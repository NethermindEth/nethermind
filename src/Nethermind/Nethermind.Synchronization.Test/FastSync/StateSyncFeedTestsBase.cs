// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
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
        private const int TimeoutLength = 5000;

        private static IBlockTree? _blockTree;
        protected static IBlockTree BlockTree => LazyInitializer.EnsureInitialized(ref _blockTree, () => Build.A.BlockTree().OfChainLength(100).TestObject);

        protected ILogger _logger = null!;
        protected ILogManager _logManager = null!;

        private readonly int _defaultPeerCount;
        private readonly int _defaultPeerMaxRandomLatency;

        public StateSyncFeedTestsBase(int defaultPeerCount = 1, int defaultPeerMaxRandomLatency = 0)
        {
            _defaultPeerCount = defaultPeerCount;
            _defaultPeerMaxRandomLatency = defaultPeerMaxRandomLatency;
        }

        public static (string Name, Action<StateTree, ITrieStore, IDb> Action)[] Scenarios => TrieScenarios.Scenarios;

        [SetUp]
        public void Setup()
        {
            _logManager = LimboLogs.Instance;
            _logger = LimboTraceLogger.Instance;
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

        protected SafeContext PrepareDownloaderWithPeer(DbContext dbContext, IEnumerable<ISyncPeer> syncPeers)
        {
            SafeContext ctx = new();
            BlockTree blockTree = Build.A.BlockTree().OfChainLength((int)BlockTree.BestSuggestedHeader!.Number).TestObject;
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            ctx.Pool = new SyncPeerPool(blockTree, new NodeStatsManager(timerFactory, LimboLogs.Instance), new TotalDifficultyBetterPeerStrategy(LimboLogs.Instance), LimboLogs.Instance, 25);
            ctx.Pool.Start();

            foreach (ISyncPeer syncPeer in syncPeers)
            {
                ctx.Pool.AddPeer(syncPeer);
            }

            ctx.SyncModeSelector = StaticSelector.StateNodesWithFastBlocks;
            ctx.TreeFeed = new(SyncMode.StateNodes, dbContext.LocalCodeDb, dbContext.LocalStateDb, blockTree, _logManager);
            ctx.Feed = new StateSyncFeed(ctx.SyncModeSelector, ctx.TreeFeed, _logManager);
            ctx.Downloader = new StateSyncDownloader(_logManager);
            ctx.StateSyncDispatcher = new SyncDispatcher<StateSyncBatch>(
                0,
                ctx.Feed!,
                ctx.Downloader,
                ctx.Pool,
                new StateSyncAllocationStrategyFactory(),
                _logManager);
            Task _ = ctx.StateSyncDispatcher.Start(CancellationToken.None);
            return ctx;
        }

        protected async Task ActivateAndWait(SafeContext safeContext, DbContext dbContext, long blockNumber, int timeout = TimeoutLength)
        {
            DotNetty.Common.Concurrency.TaskCompletionSource dormantAgainSource = new();
            safeContext.Feed.StateChanged += (_, e) =>
            {
                if (e.NewState == SyncFeedState.Dormant)
                {
                    dormantAgainSource.TrySetResult(0);
                }
            };

            safeContext.TreeFeed.ResetStateRoot(blockNumber, dbContext.RemoteStateTree.RootHash, safeContext.Feed.CurrentState);
            safeContext.Feed.Activate();

            await Task.WhenAny(
                dormantAgainSource.Task,
                Task.Delay(timeout));
        }

        protected class SafeContext
        {
            public ISyncModeSelector SyncModeSelector { get; set; } = null!;
            public SyncPeerMock[] SyncPeerMocks { get; set; } = null!;
            public ISyncPeerPool Pool { get; set; } = null!;
            public TreeSync TreeFeed { get; set; } = null!;
            public StateSyncFeed Feed { get; set; } = null!;
            public StateSyncDownloader Downloader { get; set; } = null!;
            public SyncDispatcher<StateSyncBatch> StateSyncDispatcher { get; set; } = null!;
        }

        protected class DbContext
        {
            private readonly ILogger _logger;

            public DbContext(ILogger logger, ILogManager logManager)
            {
                _logger = logger;
                RemoteDb = new MemDb();
                LocalDb = new TestMemDb();
                RemoteStateDb = RemoteDb;
                LocalStateDb = LocalDb;
                LocalCodeDb = new TestMemDb();
                RemoteCodeDb = new MemDb();
                RemoteTrieStore = new TrieStore(RemoteStateDb, logManager);

                RemoteStateTree = new StateTree(RemoteTrieStore, logManager);
                LocalStateTree = new StateTree(new TrieStore(LocalStateDb, logManager), logManager);
            }

            public MemDb RemoteCodeDb { get; }
            public TestMemDb LocalCodeDb { get; }
            public MemDb RemoteDb { get; }
            public TestMemDb LocalDb { get; }
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
                    Assert.That(local, Is.EqualTo(remote), $"{remote}{Environment.NewLine}{local}");
                    TrieStatsCollector collector = new(LocalCodeDb, LimboLogs.Instance);
                    LocalStateTree.Accept(collector, LocalStateTree.RootHash);
                    Assert.That(collector.Stats.MissingNodes, Is.EqualTo(0));
                    Assert.That(collector.Stats.MissingCode, Is.EqualTo(0));
                }
            }

            public void AssertFlushed()
            {
                LocalDb.WasFlushed.Should().BeTrue();
                LocalCodeDb.WasFlushed.Should().BeTrue();
            }
        }

        protected class SyncPeerMock : ISyncPeer
        {
            public string Name => "Mock";

            private readonly IDb _codeDb;
            private readonly IDb _stateDb;

            private Keccak[]? _filter;
            private readonly Func<IReadOnlyList<Keccak>, Task<byte[][]>>? _executorResultFunction;
            private readonly long _maxRandomizedLatencyMs;

            public SyncPeerMock(
                IDb stateDb,
                IDb codeDb,
                Func<IReadOnlyList<Keccak>, Task<byte[][]>>? executorResultFunction = null,
                long? maxRandomizedLatencyMs = null,
                Node? node = null
            )
            {
                _stateDb = stateDb;
                _codeDb = codeDb;
                _executorResultFunction = executorResultFunction;

                Node = node ?? new Node(TestItem.PublicKeyA, "127.0.0.1", 30302, true) { EthDetails = "eth66" };
                _maxRandomizedLatencyMs = maxRandomizedLatencyMs ?? 0;
            }

            public int MaxResponseLength { get; set; } = int.MaxValue;
            public Keccak HeadHash { get; set; } = null!;
            public string ProtocolCode { get; } = null!;
            public byte ProtocolVersion { get; } = default;
            public string ClientId => "executorMock";
            public Node Node { get; }
            public long HeadNumber { get; set; }
            public UInt256 TotalDifficulty { get; set; }
            public bool IsInitialized { get; set; }
            public bool IsPriority { get; set; }

            public PublicKey Id => Node.Id;

            public async Task<byte[][]> GetNodeData(IReadOnlyList<Keccak> hashes, CancellationToken token)
            {
                if (_maxRandomizedLatencyMs != 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(TestContext.CurrentContext.Random.NextLong() % _maxRandomizedLatencyMs));
                }

                if (_executorResultFunction is not null) return await _executorResultFunction(hashes);

                byte[][] responses = new byte[hashes.Count][];

                int i = 0;
                foreach (Keccak item in hashes)
                {
                    if (i >= MaxResponseLength) break;

                    if (_filter is null || _filter.Contains(item)) responses[i] = _stateDb[item.Bytes] ?? _codeDb[item.Bytes]!;

                    i++;
                }

                return responses;
            }

            public void SetFilter(Keccak[] availableHashes)
            {
                _filter = availableHashes;
            }

            public bool TryGetSatelliteProtocol<T>(string protocol, out T protocolHandler) where T : class
            {
                protocolHandler = null!;
                return false;
            }

            public void RegisterSatelliteProtocol<T>(string protocol, T protocolHandler) where T : class
            {
                throw new NotImplementedException();
            }

            public void Disconnect(DisconnectReason reason, string details)
            {
                throw new NotImplementedException();
            }

            public Task<OwnedBlockBodies> GetBlockBodies(IReadOnlyList<Keccak> blockHashes, CancellationToken token)
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

            public void SendNewTransactions(IEnumerable<Transaction> txs, bool sendFullTx)
            {
                throw new NotImplementedException();
            }

            public Task<TxReceipt[]?[]> GetReceipts(IReadOnlyList<Keccak> blockHash, CancellationToken token)
            {
                throw new NotImplementedException();
            }

        }
    }
}
