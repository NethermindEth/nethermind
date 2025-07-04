// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Core.Utils;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.Subprotocols.Snap;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.State.SnapServer;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Test.ParallelSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync;

public abstract class StateSyncFeedTestsBase
{
    public const int TimeoutLength = 20000;

    private static IBlockTree? _blockTree;
    protected static IBlockTree BlockTree => LazyInitializer.EnsureInitialized(ref _blockTree, static () => Build.A.BlockTree().OfChainLength(100).TestObject);

    protected ILogger _logger;
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
        (_logger.UnderlyingLogger as ConsoleAsyncLogger)?.Flush();
    }

    protected static StorageTree SetStorage(ITrieStore trieStore, byte i, Address address)
    {
        StorageTree remoteStorageTree = new StorageTree(trieStore.GetTrieStore(address), Keccak.EmptyTreeHash, LimboLogs.Instance);
        for (int j = 0; j < i; j++) remoteStorageTree.Set((UInt256)j, [(byte)j, i]);

        remoteStorageTree.Commit();
        return remoteStorageTree;
    }

    protected IContainer PrepareDownloader(DbContext dbContext, Action<SyncPeerMock>? mockMutator = null, int syncDispatcherAllocateTimeoutMs = 10)
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

        ContainerBuilder builder = BuildTestContainerBuilder(dbContext, syncDispatcherAllocateTimeoutMs)
            .AddSingleton<SyncPeerMock[]>(syncPeers);

        builder.RegisterBuildCallback((ctx) =>
        {
            ISyncPeerPool peerPool = ctx.Resolve<ISyncPeerPool>();
            foreach (ISyncPeer syncPeer in syncPeers)
            {
                peerPool.AddPeer(syncPeer);
            }
        });

        return builder.Build();
    }

    protected ContainerBuilder BuildTestContainerBuilder(DbContext dbContext, int syncDispatcherAllocateTimeoutMs = 10)
    {
        ContainerBuilder containerBuilder = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new ConfigProvider(new SyncConfig()
            {
                FastSync = true
            })))
            .AddDecorator<ISyncConfig>((_, syncConfig) => // Need to be a decorator because `TestEnvironmentModule` override `SyncDispatcherAllocateTimeoutMs` for other tests, but we need specific value.
            {
                syncConfig.SyncDispatcherAllocateTimeoutMs = syncDispatcherAllocateTimeoutMs; // there is a test for requested nodes which get affected if allocate timeout
                return syncConfig;
            })
            .AddSingleton<ILogManager>(_logManager)
            .AddKeyedSingleton<IDb>(DbNames.Code, dbContext.LocalCodeDb)
            .AddKeyedSingleton<IDb>(DbNames.State, dbContext.LocalStateDb)
            .AddSingleton<INodeStorage>(dbContext.LocalNodeStorage)

            // Use factory function to make it lazy in case test need to replace IBlockTree
            .AddSingleton<IBlockTree>((ctx) => CachedBlockTreeBuilder.BuildCached(
                $"{nameof(StateSyncFeedTestsBase)}{dbContext.RemoteStateTree.RootHash}{BlockTree.BestSuggestedHeader!.Number}",
                () => Build.A.BlockTree().WithStateRoot(dbContext.RemoteStateTree.RootHash).OfChainLength((int)BlockTree.BestSuggestedHeader!.Number)))

            .Add<SafeContext>();

        containerBuilder.RegisterBuildCallback((ctx) =>
        {
            ctx.Resolve<ISyncPeerPool>().Start();
        });

        return containerBuilder;
    }

    protected async Task ActivateAndWait(SafeContext safeContext, int timeout = TimeoutLength)
    {
        // Note: The `RunContinuationsAsynchronously` is very important, or the thread might continue synchronously
        // which causes unexpected hang.
        TaskCompletionSource dormantAgainSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        safeContext.Feed.StateChanged += (_, e) =>
        {
            if (e.NewState == SyncFeedState.Dormant)
            {
                dormantAgainSource.TrySetResult();
            }
        };

        safeContext.Feed.SyncModeSelectorOnChanged(SyncMode.StateNodes | SyncMode.FastBlocks);
        safeContext.StartDispatcher(safeContext.CancellationToken);

        await Task.WhenAny(
            dormantAgainSource.Task,
            Task.Delay(timeout));
    }

    protected class SafeContext(
        Lazy<SyncPeerMock[]> syncPeerMocks,
        Lazy<ISyncPeerPool> syncPeerPool,
        Lazy<TreeSync> treeSync,
        Lazy<StateSyncFeed> stateSyncFeed,
        Lazy<SyncDispatcher<StateSyncBatch>> syncDispatcher,
        IBlockTree blockTree
    ) : IDisposable
    {
        public SyncPeerMock[] SyncPeerMocks => syncPeerMocks.Value;
        public ISyncPeerPool Pool => syncPeerPool.Value;
        public TreeSync TreeFeed => treeSync.Value;
        public StateSyncFeed Feed => stateSyncFeed.Value;

        private readonly AutoCancelTokenSource _autoCancelTokenSource = new AutoCancelTokenSource();
        public CancellationToken CancellationToken => _autoCancelTokenSource.Token;

        private bool _isDisposed;

        public void SuggestBlocksWithUpdatedRootHash(Hash256 newRootHash)
        {
            Block newBlock = Build.A.Block
                .WithParent(blockTree.BestSuggestedHeader!)
                .WithStateRoot(newRootHash)
                .TestObject;

            blockTree.SuggestBlock(newBlock).Should().Be(AddBlockResult.Added);
            blockTree.UpdateMainChain([newBlock], false, true);
        }

        public void StartDispatcher(CancellationToken cancellationToken)
        {
            Task _ = syncDispatcher.Value.Start(cancellationToken);
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _autoCancelTokenSource.Dispose();
        }
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
            LocalNodeStorage = new NodeStorage(LocalDb);
            LocalCodeDb = new TestMemDb();
            RemoteCodeDb = new MemDb();
            RemoteTrieStore = TestTrieStoreFactory.Build(RemoteStateDb, logManager);

            RemoteStateTree = new StateTree(RemoteTrieStore, logManager);
            LocalStateTree = new StateTree(TestTrieStoreFactory.Build(LocalStateDb, logManager), logManager);
        }

        public MemDb RemoteCodeDb { get; }
        public TestMemDb LocalCodeDb { get; }
        public MemDb RemoteDb { get; }
        public TestMemDb LocalDb { get; }
        public ITrieStore RemoteTrieStore { get; }
        public IDb RemoteStateDb { get; }
        public IDb LocalStateDb { get; }
        public NodeStorage LocalNodeStorage { get; }
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
                Assert.That(local, Is.EqualTo(remote), $"{stage}{Environment.NewLine}{remote}{Environment.NewLine}{local}");
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

    protected class SyncPeerMock : BaseSyncPeerMock
    {
        public override string Name => "Mock";

        private readonly IDb _codeDb;
        private readonly IReadOnlyKeyValueStore _stateDb;
        private readonly ISnapServer _snapServer;

        private Hash256[]? _filter;
        private readonly Func<IReadOnlyList<Hash256>, Task<IOwnedReadOnlyList<byte[]>>>? _executorResultFunction;
        private readonly long _maxRandomizedLatencyMs;

        public SyncPeerMock(
            IDb stateDb,
            IDb codeDb,
            Func<IReadOnlyList<Hash256>, Task<IOwnedReadOnlyList<byte[]>>>? executorResultFunction = null,
            long? maxRandomizedLatencyMs = null,
            Node? node = null
        )
        {
            _codeDb = codeDb;
            _executorResultFunction = executorResultFunction;

            Node = node ?? new Node(TestItem.PublicKeyA, "127.0.0.1", 30302, true) { EthDetails = "eth67" };
            _maxRandomizedLatencyMs = maxRandomizedLatencyMs ?? 0;

            IStateReader alwaysAvailableRootTracker = Substitute.For<IStateReader>();
            alwaysAvailableRootTracker.HasStateForRoot(Arg.Any<Hash256>()).Returns(true);
            TrieStore trieStore = new TrieStore(new NodeStorage(stateDb), Nethermind.Trie.Pruning.No.Pruning,
                Persist.EveryBlock, new PruningConfig(), LimboLogs.Instance);
            _stateDb = trieStore.TrieNodeRlpStore;
            _snapServer = new SnapServer(
                trieStore.AsReadOnly(),
                codeDb,
                alwaysAvailableRootTracker,
                LimboLogs.Instance);
        }

        public int MaxResponseLength { get; set; } = int.MaxValue;
        public override byte ProtocolVersion { get; } = 67;
        public override string ClientId => "executorMock";
        public override PublicKey Id => Node.Id;

        public override async Task<IOwnedReadOnlyList<byte[]>> GetNodeData(IReadOnlyList<Hash256> hashes, CancellationToken token)
        {
            if (_maxRandomizedLatencyMs != 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(TestContext.CurrentContext.Random.NextLong() % _maxRandomizedLatencyMs));
            }

            if (_executorResultFunction is not null) return await _executorResultFunction(hashes);

            ArrayPoolList<byte[]> responses = new(hashes.Count, hashes.Count);

            int i = 0;
            foreach (Hash256 item in hashes)
            {
                if (i >= MaxResponseLength) break;

                if (_filter is null || _filter.Contains(item))
                {
                    responses[i] = _codeDb[item.Bytes] ?? _stateDb[item.Bytes]!;
                }

                i++;
            }

            return responses;
        }

        public void SetFilter(Hash256[]? availableHashes)
        {
            _filter = availableHashes;
        }

        public override bool TryGetSatelliteProtocol<T>(string protocol, out T protocolHandler) where T : class
        {
            if (protocol == Protocol.Snap)
            {
                protocolHandler = (this as T)!;
                return true;
            }
            protocolHandler = null!;
            return false;
        }

        public override Task<BlockHeader?> GetHeadBlockHeader(Hash256? hash, CancellationToken token)
        {
            return Task.FromResult(BlockTree.Head?.Header);
        }

        public override Task<IOwnedReadOnlyList<byte[]>> GetByteCodes(IReadOnlyList<ValueHash256> codeHashes, CancellationToken token)
        {
            return Task.FromResult(_snapServer.GetByteCodes(codeHashes, long.MaxValue, token));
        }

        public override Task<IOwnedReadOnlyList<byte[]>> GetTrieNodes(AccountsToRefreshRequest request, CancellationToken token)
        {
            IOwnedReadOnlyList<PathGroup> groups = SnapProtocolHandler.GetPathGroups(request);
            return GetTrieNodes(new GetTrieNodesRequest()
            {
                RootHash = request.RootHash,
                AccountAndStoragePaths = groups,
            }, token);
        }

        public override Task<IOwnedReadOnlyList<byte[]>> GetTrieNodes(GetTrieNodesRequest request, CancellationToken token)
        {
            var nodes = _snapServer.GetTrieNodes(request.AccountAndStoragePaths, request.RootHash, token);
            return Task.FromResult(nodes!);
        }
    }
}
