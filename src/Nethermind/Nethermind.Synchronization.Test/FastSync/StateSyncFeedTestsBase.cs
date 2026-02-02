// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Features.AttributeFilters;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
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
using Nethermind.Synchronization.SnapSync;
using Nethermind.Synchronization.Test.ParallelSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync;

public abstract class StateSyncFeedTestsBase(
    Action<ContainerBuilder> registerTreeSyncStore,
    int defaultPeerCount = 1,
    int defaultPeerMaxRandomLatency = 0)
{
    public const int TimeoutLength = 20000;

    // Chain length used for test block trees, use a constant to avoid shared state
    private const int TestChainLength = 100;

    protected ILogger _logger;
    protected ILogManager _logManager = null!;

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

    protected IContainer PrepareDownloader(RemoteDbContext remote, Action<SyncPeerMock>? mockMutator = null, int syncDispatcherAllocateTimeoutMs = 10)
    {
        SyncPeerMock[] syncPeers = new SyncPeerMock[defaultPeerCount];
        for (int i = 0; i < defaultPeerCount; i++)
        {
            Node node = new Node(TestItem.PublicKeys[i], $"127.0.0.{i}", 30302, true)
            {
                EthDetails = "eth68",
            };
            SyncPeerMock mock = new SyncPeerMock(remote.StateDb, remote.CodeDb, node: node, maxRandomizedLatencyMs: defaultPeerMaxRandomLatency);
            mockMutator?.Invoke(mock);
            syncPeers[i] = mock;
        }

        ContainerBuilder builder = BuildTestContainerBuilder(remote, syncDispatcherAllocateTimeoutMs)
            .AddSingleton<SyncPeerMock[]>(syncPeers);

        builder.RegisterBuildCallback((ctx) =>
        {
            IBlockTree blockTree = ctx.Resolve<IBlockTree>();
            ISyncPeerPool peerPool = ctx.Resolve<ISyncPeerPool>();
            foreach (SyncPeerMock syncPeer in syncPeers)
            {
                // Set per-test block tree to avoid race conditions during parallel execution
                syncPeer.SetBlockTree(blockTree);
                peerPool.AddPeer(syncPeer);
            }
        });

        return builder.Build();
    }

    protected ContainerBuilder BuildTestContainerBuilder(RemoteDbContext remote, int syncDispatcherAllocateTimeoutMs = 10)
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
            .AddSingleton<INodeStorage>((ctx) => new NodeStorage(ctx.ResolveNamed<IDb>(DbNames.State)))
            .AddSingleton<ISnapTrieFactory, PatriciaSnapTrieFactory>()
            .AddSingleton<LocalDbContext>()
            .AddKeyedSingleton<IDb>(DbNames.Code, (_) => new TestMemDb())
            .AddKeyedSingleton<IDb>(DbNames.State, (_) => new TestMemDb())

            // Use factory function to make it lazy in case test need to replace IBlockTree
            // Cache key includes type name so different inherited test classes don't share the same blocktree
            .AddSingleton<IBlockTree>((ctx) => CachedBlockTreeBuilder.BuildCached(
                $"{GetType().Name}{remote.StateTree.RootHash}{TestChainLength}",
                () => Build.A.BlockTree().WithStateRoot(remote.StateTree.RootHash).OfChainLength(TestChainLength)))

            .Add<SafeContext>();

        registerTreeSyncStore(containerBuilder);

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
        Lazy<ISyncDownloader<StateSyncBatch>> downloader,
        Lazy<SyncDispatcher<StateSyncBatch>> syncDispatcher,
        Lazy<IBlockProcessingQueue> blockProcessingQueue,
        IBlockTree blockTree
    ) : IDisposable
    {
        public SyncPeerMock[] SyncPeerMocks => syncPeerMocks.Value;
        public ISyncPeerPool Pool => syncPeerPool.Value;
        public TreeSync TreeFeed => treeSync.Value;
        public StateSyncFeed Feed => stateSyncFeed.Value;
        public IBlockProcessingQueue BlockProcessingQueue => blockProcessingQueue.Value;

        public ISyncDownloader<StateSyncBatch> Downloader => downloader.Value;

        private readonly AutoCancelTokenSource _autoCancelTokenSource = new();
        public CancellationToken CancellationToken => _autoCancelTokenSource.Token;

        private bool _isDisposed;

        public async Task SuggestBlocksWithUpdatedRootHash(Hash256 newRootHash)
        {
            Block newBlock = Build.A.Block
                .WithParent(blockTree.BestSuggestedHeader!)
                .WithStateRoot(newRootHash)
                .TestObject;

            (await blockTree.SuggestBlockAsync(newBlock)).Should().Be(AddBlockResult.Added);
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

    protected class LocalDbContext
    {
        public LocalDbContext(
            [KeyFilter(DbNames.Code)] IDb codeDb,
            [KeyFilter(DbNames.State)] IDb stateDb,
            INodeStorage nodeStorage,
            ILogManager logManager)
        {
            NodeStorage = nodeStorage;
            CodeDb = (TestMemDb)codeDb;
            Db = (TestMemDb)stateDb;
            StateTree = new StateTree(TestTrieStoreFactory.Build(nodeStorage, logManager), logManager);
        }

        private TestMemDb CodeDb { get; }
        private TestMemDb Db { get; }
        private INodeStorage NodeStorage { get; }
        private StateTree StateTree { get; }

        public Hash256 RootHash
        {
            get => StateTree.RootHash;
            set => StateTree.RootHash = value;
        }

        public void UpdateRootHash() => StateTree.UpdateRootHash();

        public void Set(Hash256 address, Account? account) => StateTree.Set(address, account);

        public void Commit() => StateTree.Commit();

        public void AssertFlushed()
        {
            Db.WasFlushed.Should().BeTrue();
            CodeDb.WasFlushed.Should().BeTrue();
        }

        public void CompareTrees(RemoteDbContext remote, ILogger logger, string stage, bool skipLogs = false)
        {
            if (!skipLogs) logger.Info($"==================== {stage} ====================");
            StateTree.RootHash = remote.StateTree.RootHash;

            if (!skipLogs) logger.Info("-------------------- REMOTE --------------------");
            TreeDumper dumper = new TreeDumper();
            remote.StateTree.Accept(dumper, remote.StateTree.RootHash);
            string remoteStr = dumper.ToString();
            if (!skipLogs) logger.Info(remoteStr);
            if (!skipLogs) logger.Info("-------------------- LOCAL --------------------");
            dumper.Reset();
            StateTree.Accept(dumper, StateTree.RootHash);
            string localStr = dumper.ToString();
            if (!skipLogs) logger.Info(localStr);

            if (stage == "END")
            {
                Assert.That(localStr, Is.EqualTo(remoteStr), $"{stage}{Environment.NewLine}{remoteStr}{Environment.NewLine}{localStr}");
                TrieStatsCollector collector = new(CodeDb, LimboLogs.Instance);
                StateTree.Accept(collector, StateTree.RootHash);
                Assert.That(collector.Stats.MissingNodes, Is.EqualTo(0));
                Assert.That(collector.Stats.MissingCode, Is.EqualTo(0));
            }
        }

        public void DeleteStateRoot()
        {
            NodeStorage.Set(null, TreePath.Empty, RootHash, null);
        }
    }

    protected class RemoteDbContext
    {
        public RemoteDbContext(ILogManager logManager)
        {
            CodeDb = new MemDb();
            Db = new MemDb();
            TrieStore = TestTrieStoreFactory.Build(Db, logManager);
            StateTree = new StateTree(TrieStore, logManager);
        }

        public MemDb CodeDb { get; }
        public MemDb Db { get; }
        public IDb StateDb => Db;
        public ITrieStore TrieStore { get; }
        public StateTree StateTree { get; }
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

        // Per-test block tree to avoid race conditions during parallel test execution
        private IBlockTree? _blockTree;

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

            Node = node ?? new Node(TestItem.PublicKeyA, "127.0.0.1", 30302, true) { EthDetails = "eth68" };
            _maxRandomizedLatencyMs = maxRandomizedLatencyMs ?? 0;

            PruningConfig pruningConfig = new PruningConfig();
            TestFinalizedStateProvider testFinalizedStateProvider = new TestFinalizedStateProvider(pruningConfig.PruningBoundary);
            TrieStore trieStore = new TrieStore(new NodeStorage(stateDb), Nethermind.Trie.Pruning.No.Pruning,
                Persist.EveryBlock, testFinalizedStateProvider, pruningConfig, LimboLogs.Instance);
            _stateDb = trieStore.TrieNodeRlpStore;
            _snapServer = new SnapServer(
                trieStore.AsReadOnly(),
                codeDb,
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

        public void SetBlockTree(IBlockTree blockTree)
        {
            _blockTree = blockTree;
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
            return Task.FromResult(_blockTree?.Head?.Header);
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
