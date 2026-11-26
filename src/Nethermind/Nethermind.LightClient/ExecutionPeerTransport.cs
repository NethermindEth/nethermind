// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Autofac;
using Autofac.Features.AttributeFilters;
using Microsoft.Extensions.Caching.Memory;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.Ethash;
using Nethermind.Consensus.Scheduler;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Init.Modules;
using Nethermind.Int256;
using Nethermind.KeyStore.Config;
using Nethermind.LightClient.Rpc;
using Nethermind.Logging;
using Nethermind.Merge.AuRa.Withdrawals;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.State.SnapServer;
using Nethermind.State.Proofs;
using Nethermind.Stats.Model;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Synchronization.StateSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;

namespace Nethermind.LightClient;

/// <summary>Connects to execution peers without starting block or state synchronization.</summary>
internal sealed class ExecutionPeerTransport : IExecutionStateSource, IAsyncDisposable
{
    private const int ExecutionRequestLimit = 4;
    private const int ExecutionBulkLimit = ExecutionRequestLimit - 1;
    private const int ConsensusHeaderRequestLimit = 2;
    private const int SnapRequestLimit = 8;
    // Two concurrent proof RPCs leave SNAP capacity for other state reads.
    private const int ProofStorageConcurrency = 3;
    private const string BlockDataUnavailable = "No execution peer supplied authenticated block data.";
    private const string AncestorChainUnavailable = "No execution peer supplied an authenticated header chain.";
    private const int ConsensusHeaderTimeoutSeconds = 3;
    private const int SlowResponseMilliseconds = 7500;
    private const int PeerFeedbackCapacity = 256;
    private static readonly TimeSpan BulkHedgeDelay = TimeSpan.FromMilliseconds(250);
    private readonly IContainer _services;
    private readonly ILogManager _logManager;
    private readonly Nethermind.Logging.ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly PayloadCache<Block> _bodies;
    private readonly PayloadCache<TxReceipt[]> _receipts;
    private readonly PayloadCache<BlockHeader> _headers;
    private readonly PayloadCache<Account> _accounts;
    private readonly PayloadCache<UInt256> _storage;
    private readonly PayloadCache<byte[]> _code;
    private readonly PayloadCache<ReadOnlyBlockAccessList> _blockAccessLists;
    private readonly SemaphoreSlim _executionDataRequests = new(ExecutionRequestLimit, ExecutionRequestLimit);
    private readonly SemaphoreSlim _executionBulkRequests = new(ExecutionBulkLimit, ExecutionBulkLimit);
    private readonly SemaphoreSlim _consensusHeaderRequests = new(ConsensusHeaderRequestLimit, ConsensusHeaderRequestLimit);
    private readonly MemoryCache _peerCooldowns = new(new MemoryCacheOptions { SizeLimit = PeerFeedbackCapacity });
    private readonly Lock _cooldownLock = new();
    private readonly ConditionalWeakTable<PeerInfo, PeerPerformance> _peerPerformance = [];
    private readonly INodeStatsManager _peerStats;
    private readonly ConcurrentDictionary<AllocationContexts, PeerInfo> _preferredPeers = new();
    private readonly ConcurrentDictionary<AllocationContexts, PeerInfo> _lastPeers = new();
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _snapRequests = new(SnapRequestLimit, SnapRequestLimit);
    private readonly LightSyncServer _syncServer;
    private readonly BoundedTransactionRelay _transactionRelay = new();
    private readonly HostedAccountState? _hostedAccounts;
    private readonly Lazy<FinalizedHeaderHistory> _logHistory;
    private readonly Lock _historyLifetime = new();
    private bool _historyStopped;
    private IRlpxHost? _host;
    private IDiscoveryApp? _discovery;
    private IPeerPool? _peerPool;
    private IPeerManager? _peerManager;
    private ISessionMonitor? _sessionMonitor;
    private Task? _feederTask;
    private Task? _snapCapacityTask;

    internal int PeerCount => _services.Resolve<ISyncPeerPool>().PeerCount;
    internal Nethermind.Core.Specs.ISpecProvider SpecProvider => _services.Resolve<Nethermind.Core.Specs.ISpecProvider>();
    internal Nethermind.Facade.Eth.IBlockForRpcFactory BlockForRpcFactory => _services.Resolve<Nethermind.Facade.Eth.IBlockForRpcFactory>();
    internal IWithdrawalProcessorFactory WithdrawalProcessorFactory => _services.Resolve<IWithdrawalProcessorFactory>();
    internal IForkInfo ForkInfo => _services.Resolve<IForkInfo>();
    internal int SnapPeerCount
    {
        get
        {
            int count = 0;
            foreach (PeerInfo peer in _services.Resolve<ISyncPeerPool>().AllPeers)
                if (peer.SyncPeer.TryGetSatelliteProtocol<ISnapSyncPeer>(Protocol.Snap, out _)) count++;
            return count;
        }
    }
    internal int CandidateCount => _peerPool?.PeerCount ?? 0;
    internal string DiscoveryStatus => _feederTask?.Status.ToString() ?? "not started";
    internal string DiscoveryDomain => _services.Resolve<INetworkConfig>().DiscoveryDns ?? "<unset>";
    internal HistoryProgress LogHistoryProgress => _logHistory.IsValueCreated ? _logHistory.Value.Progress
        : new(null, null, null, null, false, null);
    internal HostedAccountProgress[] GetHostedAccountProgress(VerifiedHead? target) =>
        _hostedAccounts?.GetProgress(target) ?? [];

    internal ExecutionPeerTransport(string network, ILogManager logManager, VerifiedHead? head = null, ulong timestamp = 0,
        string? dataDirectory = null, Address[]? hostedAccounts = null, int port = 30307,
        TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _bodies = new(BodySize, _stop.Token);
        _receipts = new(ReceiptsSize, _stop.Token);
        _headers = new(static header => 2048 + header.ExtraData.LongLength, _stop.Token);
        _accounts = new(static _ => 1024, _stop.Token);
        _storage = new(static _ => 1024, _stop.Token);
        _code = new(static bytes => bytes.Length, _stop.Token);
        _blockAccessLists = new(static list => 1024 + 4L * BlockAccessListDecoder.Instance.GetLength(list, RlpBehaviors.None)
            + 512L * list.ItemCount + 128L * list.TotalStorageChangeEvents, _stop.Token);
        _logManager = logManager;
        _logger = logManager.GetClassLogger<ExecutionPeerTransport>();
        if (hostedAccounts is { Length: > 0 })
            _hostedAccounts = new(hostedAccounts, this, DownloadStorageAsync, _logger);
        _ = typeof(EthashChainSpecEngineParameters).Assembly;
        _ = typeof(AuRaChainSpecEngineParameters).Assembly;
        TypeDiscovery.Initialize();
        (string chainSpecFile, Hash256 genesisHash) = network switch
        {
            "mainnet" => ("foundation.json", KnownHashes.MainnetGenesis),
            "hoodi" => ("hoodi.json", KnownHashes.HoodiGenesis),
            "sepolia" => ("sepolia.json", KnownHashes.SepoliaGenesis),
            "gnosis" => ("gnosis.json", KnownHashes.GnosisGenesis),
            _ => throw new ArgumentException("Supported networks: mainnet, hoodi, sepolia, gnosis.", nameof(network)),
        };
        ChainSpec chainSpec = new ChainSpecFileLoader(new EthereumJsonSerializer(), logManager)
            .LoadEmbeddedOrFromFile(chainSpecFile);
        bool isGnosis = network == "gnosis";
        if (isGnosis) AuRaChainSpecLoader.ProcessChainSpec(chainSpec);
        (chainSpec.Genesis ?? throw new InvalidOperationException("Chain spec has no genesis block")).Header.Hash = genesisHash;
        _syncServer = new LightSyncServer(chainSpec, head, timestamp);
        InitConfig init = new() { DiagnosticMode = DiagnosticMode.MemDb, DataDir = "src/Nethermind/artifacts/lightclient-execution" };
        if (dataDirectory is not null)
        {
            string executionDirectory = Path.GetFullPath(Path.Combine(dataDirectory, "el", network));
            init.DataDir = executionDirectory;
            init.BaseDbPath = Path.Combine(executionDirectory, "db");
        }
        NetworkConfig net = new()
        {
            // Gnosis has no ethdisco.net tree; its chainspec supplies discv5-capable EL bootnodes.
            DiscoveryDns = isGnosis ? string.Empty : $"enrtree://AKA3AM6LPBYEUDMVNU3BSVQJ5AD45Y7YPOHJLEF6W26QOE4VTUDPE@snap.{network}.ethdisco.net",
            P2PPort = port,
            DiscoveryPort = port,
            MaxActivePeers = 32,
            MaxCandidatePeerCount = 4096,
            CandidatePeerCountCleanupThreshold = 4500,
            IsPeersPersistenceOn = false,
            ClientIdMatcher = "(?i)^(?!.*enrscout)",
        };
        SyncConfig sync = new() { SnapSync = true, FastSync = false, SnapServingEnabled = false };
        DiscoveryConfig discovery = new()
        {
            DiscoveryVersion = isGnosis ? DiscoveryVersion.All : DiscoveryVersion.V5,
            UseDefaultDiscv5Bootnodes = !isGnosis,
        };
        KeyStoreConfig keyStore = new() { KeyStoreDirectory = Path.GetFullPath(dataDirectory ?? init.DataDir) };
        ConfigProvider config = new(init, net, sync, discovery, keyStore, new FlatDbConfig { Enabled = true });
        ContainerBuilder builder = new();
        builder.RegisterModule(new NethermindModule(chainSpec, config, logManager));
        if (isGnosis)
        {
            builder.AddModule(new AuRaHeaderModule())
                .AddSingleton(chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<AuRaChainSpecEngineParameters>())
                .AddModule(new AuRaWithdrawalModule());
        }
        builder.RegisterModule(new LightExecutionOverrides(_syncServer, _transactionRelay, _stop.Token));
        if (dataDirectory is not null)
        {
            string directory = Path.Combine(dataDirectory, "el", network, "discovery");
            builder.Register(context => BoundedEnrStorage.OpenDb(directory, context.Resolve<ILogManager>()))
                .Keyed<IFullDb>(DbNames.DiscoveryV5Nodes).SingleInstance();
            builder.Register(context => new BoundedEnrStorage(new NetworkStorage(
                context.ResolveKeyed<IFullDb>(DbNames.DiscoveryV5Nodes), context.Resolve<ILogManager>())))
                .Keyed<INetworkStorage>(DbNames.DiscoveryV5Nodes).SingleInstance();
        }
        _services = builder.Build();
        try
        {
            _peerStats = _services.Resolve<INodeStatsManager>();
            if (head is null || head.Number == 0 && head.BlockHash != genesisHash)
            {
                using ILifetimeScope scope = _services.BeginLifetimeScope(scopeBuilder => scopeBuilder
                    .AddSingleton<IWorldStateScopeProvider>(_services.Resolve<IWorldStateManager>().GlobalWorldState));
                using IDisposable state = scope.Resolve<IWorldState>().BeginScope(IWorldState.PreGenesis);
                Block genesis = scope.Resolve<IGenesisBuilder>().Build();
                if (genesis.Hash != genesisHash)
                    throw new InvalidDataException("Built execution genesis does not match the selected network.");
                genesis.Header.TotalDifficulty = genesis.Difficulty;
            }
        }
        catch
        {
            _services.Dispose();
            throw;
        }
        _logHistory = new(() =>
        {
            string? directory = dataDirectory is null ? null : Path.GetFullPath(Path.Combine(dataDirectory, "el", network, "log-history"));
            IDb db = directory is null ? new MemDb("lightClientLogHeaders") : FinalizedHeaderHistory.OpenDb(directory, logManager);
            try
            {
                return new(db, genesisHash, _services.Resolve<IHeaderDecoder>(), GetHeaderAsync, FetchLogHeaderPageAsync, _logger,
                    clock: _clock, directory: directory, prefetchPages: BeginLogHeaderPages);
            }
            catch { db.Dispose(); throw; }
        });
    }

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _services.Resolve<IIPResolver>().Resolve(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        IBlockTree blockTree = _services.Resolve<IBlockTree>();
        ChainSpec chainSpec = _services.Resolve<ChainSpec>();
        Block genesis = chainSpec.Genesis!;
        blockTree.SuggestBlock(genesis, BlockTreeSuggestOptions.None);

        _services.Resolve<IProtocolsManager>();
        _host = _services.Resolve<IRlpxHost>();
        await _host.Init();
        cancellationToken.ThrowIfCancellationRequested();
        _services.Resolve<ISyncPeerPool>().Start();
        _discovery = _services.Resolve<IDiscoveryApp>();
        await _discovery.StartAsync();
        cancellationToken.ThrowIfCancellationRequested();
        _feederTask = _services.Resolve<NodeSourceToDiscV4Feeder>().Run();
        _peerPool = _services.Resolve<IPeerPool>();
        _peerPool.Start();
        _peerManager = _services.Resolve<IPeerManager>();
        _peerManager.Start();
        _sessionMonitor = _services.Resolve<ISessionMonitor>();
        _sessionMonitor.Start();
        _snapCapacityTask = MaintainSnapCapacityAsync();
    }

    private async Task MaintainSnapCapacityAsync()
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(10), _clock);
        ISyncPeerPool pool = _services.Resolve<ISyncPeerPool>();
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                int availableSnap = 0;
                foreach (PeerInfo peer in pool.InitializedPeers)
                {
                    if (!IsCooling(peer, AllocationContexts.Snap) && !IsSlow(peer, AllocationContexts.Snap)
                        && peer.SyncPeer.TryGetSatelliteProtocol<ISnapSyncPeer>(Protocol.Snap, out _)) availableSnap++;
                }
                if (availableSnap >= 8 || pool.InitializedPeersCount < 30) continue;
                PeerInfo? victim = null;
                foreach (PeerInfo peer in pool.InitializedPeers)
                {
                    bool snap = peer.SyncPeer.TryGetSatelliteProtocol<ISnapSyncPeer>(Protocol.Snap, out _);
                    if (peer.SyncPeer.IsPriority || snap && !IsCooling(peer, AllocationContexts.Snap)
                        && !IsSlow(peer, AllocationContexts.Snap)) continue;
                    if (!snap)
                    {
                        victim = peer;
                        break;
                    }
                    if (victim is null || PeerLatency(peer, AllocationContexts.Snap) > PeerLatency(victim, AllocationContexts.Snap)) victim = peer;
                }
                victim?.SyncPeer.Disconnect(DisconnectReason.DropWorstPeer, "Reserving light-client connection capacity for SNAP peers");
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.Error("Execution SNAP capacity maintenance stopped", exception);
        }
    }

    internal void UpdateHead(VerifiedHead head, ulong timestamp)
    {
        if (_stop.IsCancellationRequested) return;
        byte[]? encodedHeader = _headers.TryGetValue(head.BlockHash, out BlockHeader? retained)
            ? _services.Resolve<IHeaderDecoder>().Encode(retained) : null;
        _syncServer.UpdateHead(head, timestamp, encodedHeader);
        BlockHeader current = _syncServer.Head;
        foreach (PeerInfo peer in _services.Resolve<ISyncPeerPool>().InitializedPeers)
        {
            if (peer.SyncPeer.ProtocolVersion < 69) continue;
            try { peer.SyncPeer.NotifyOfNewRange(current, current); }
            catch (Exception exception)
            {
                if (_logger.IsDebug) _logger.Debug($"Execution range announcement to {peer.SyncPeer.Node:c} failed: {exception.Message}");
            }
        }
    }

    internal Task HostAccountsAsync(Func<VerifiedHead?> getHead, TimeProvider clock, CancellationToken cancellationToken) =>
        _hostedAccounts?.RunAsync(getHead, clock, cancellationToken) ?? Task.Delay(Timeout.Infinite, cancellationToken);

    internal void NotifyHostedHeadChanged() => _hostedAccounts?.NotifyHeadChanged();

    public Task<Account> GetAccountAsync(VerifiedHead head, Address address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool trace = _logger.IsDebug;
        if (_hostedAccounts?.TryGetAccount(head, address, out Account retained) == true)
            return Task.FromResult(retained);
        ValueHash256 path = ValueKeccak.Compute(address.Bytes);
        return _accounts.GetAsync(StateKey(head.StateRoot, address.Bytes), fetchToken => RequestAsync(async (peer, token) =>
        {
            long responseStart = trace ? _clock.GetTimestamp() : 0;
            AccountsAndProofs response;
            try { response = await peer.GetAccountRange(new AccountRange(head.StateRoot, path, path.IncrementPath()), token); }
            catch
            {
                if (trace)
                    _logger.Debug($"LC peer id={Activity.Current?.Id} phase=response-await kind=account outcome=failed elapsedMs={_clock.GetElapsedTime(responseStart).TotalMilliseconds:F3}");
                throw;
            }
            using (response)
            {
                if (trace)
                    _logger.Debug($"LC peer id={Activity.Current?.Id} phase=response-await kind=account outcome=received block={head.BlockHash} stateRoot={head.StateRoot} elapsedMs={_clock.GetElapsedTime(responseStart).TotalMilliseconds:F3}");
                long verificationStart = trace ? _clock.GetTimestamp() : 0;
                bool verified = false;
                try
                {
                    Account result = VerifyAccountRange(head.StateRoot, address, response, _logManager);
                    verified = true;
                    return result;
                }
                finally
                {
                    if (trace)
                        _logger.Debug($"LC peer id={Activity.Current?.Id} phase=verification kind=account outcome={(verified ? "verified" : "failed")} elapsedMs={_clock.GetElapsedTime(verificationStart).TotalMilliseconds:F3}");
                }
            }
        }, fetchToken, "account"), cancellationToken);
    }

    public Task<UInt256> GetStorageAsync(VerifiedHead head, Address address, Account account, UInt256 key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool trace = _logger.IsDebug;
        if (_hostedAccounts?.TryGetStorage(address, account, key, out UInt256 retained) == true)
            return Task.FromResult(retained);
        if (!account.HasStorage) return Task.FromResult(UInt256.Zero);
        ValueHash256 accountPath = ValueKeccak.Compute(address.Bytes);
        Span<byte> slotBytes = stackalloc byte[32];
        key.ToBigEndian(slotBytes);
        ValueHash256 slotPath = ValueKeccak.Compute(slotBytes);
        return _storage.GetAsync(StateKey(account.StorageRoot, slotBytes), fetchToken => RequestAsync(async (peer, token) =>
        {
            using StorageRange request = new()
            {
                RootHash = head.StateRoot,
                Accounts = new ArrayPoolList<PathWithAccount>(1) { new(accountPath, account.ToStruct()) },
                StartingHash = slotPath,
                LimitHash = slotPath.IncrementPath(),
            };
            long responseStart = trace ? _clock.GetTimestamp() : 0;
            SlotsAndProofs response;
            try { response = await peer.GetStorageRange(request, token); }
            catch
            {
                if (trace)
                    _logger.Debug($"LC peer id={Activity.Current?.Id} phase=response-await kind=storage outcome=failed elapsedMs={_clock.GetElapsedTime(responseStart).TotalMilliseconds:F3}");
                throw;
            }
            using (response)
            {
                if (trace)
                    _logger.Debug($"LC peer id={Activity.Current?.Id} phase=response-await kind=storage outcome=received block={head.BlockHash} stateRoot={head.StateRoot} storageRoot={account.StorageRoot} elapsedMs={_clock.GetElapsedTime(responseStart).TotalMilliseconds:F3}");
                long verificationStart = trace ? _clock.GetTimestamp() : 0;
                bool verified = false;
                try
                {
                    UInt256 result = VerifyStorageRange(account, accountPath, key, response, _logManager);
                    verified = true;
                    return result;
                }
                finally
                {
                    if (trace)
                        _logger.Debug($"LC peer id={Activity.Current?.Id} phase=verification kind=storage outcome={(verified ? "verified" : "failed")} elapsedMs={_clock.GetElapsedTime(verificationStart).TotalMilliseconds:F3}");
                }
            }
        }, fetchToken, "storage"), cancellationToken);
    }

    public async Task<byte[]> GetCodeAsync(Account account, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool trace = _logger.IsDebug;
        if (_hostedAccounts?.TryGetCode(account, out byte[] retained) == true)
            return retained;
        if (!account.HasCode) return [];
        byte[] cached = await _code.GetAsync(account.CodeHash, fetchToken => RequestAsync(async (peer, token) =>
        {
            long responseStart = trace ? _clock.GetTimestamp() : 0;
            IByteArrayList response;
            try { response = await peer.GetByteCodes([new ValueHash256(account.CodeHash.Bytes)], token); }
            catch
            {
                if (trace)
                    _logger.Debug($"LC peer id={Activity.Current?.Id} phase=response-await kind=code outcome=failed elapsedMs={_clock.GetElapsedTime(responseStart).TotalMilliseconds:F3}");
                throw;
            }
            using (response)
            {
                if (trace)
                    _logger.Debug($"LC peer id={Activity.Current?.Id} phase=response-await kind=code outcome=received codeHash={account.CodeHash} elapsedMs={_clock.GetElapsedTime(responseStart).TotalMilliseconds:F3}");
                long verificationStart = trace ? _clock.GetTimestamp() : 0;
                bool verified = false;
                try
                {
                    if (response.Count == 0)
                        throw new ExecutionDataUnavailableException("Execution peer does not have the requested bytecode.");
                    if (response.Count != 1 || response[0].Length > 1024 * 1024)
                        throw new InvalidDataException("Execution peer returned an invalid bytecode response.");
                    byte[] result = response[0].ToArray();
                    ExecutionProofVerifier.VerifyCode(account.CodeHash, result);
                    verified = true;
                    return result;
                }
                finally
                {
                    if (trace)
                        _logger.Debug($"LC peer id={Activity.Current?.Id} phase=verification kind=code outcome={(verified ? "verified" : "failed")} elapsedMs={_clock.GetElapsedTime(verificationStart).TotalMilliseconds:F3}");
                }
            }
        }, fetchToken, "code"), cancellationToken);
        return cached.AsSpan().ToArray();
    }

    private static Hash256 StateKey(Hash256 root, ReadOnlySpan<byte> path)
    {
        Span<byte> key = stackalloc byte[32 + path.Length];
        root.Bytes.CopyTo(key);
        path.CopyTo(key[32..]);
        return Keccak.Compute(key);
    }

    public Task<IReadOnlyDictionary<Hash256, byte[]>> GetStorageTrieNodesAsync(VerifiedHead head, Address address,
        StorageTrieNodeRequest[] requests, CancellationToken cancellationToken)
    {
        if (requests.Length is 0 or > 256)
            throw new ArgumentOutOfRangeException(nameof(requests));
        byte[][] paths = new byte[requests.Length + 1][];
        paths[0] = Keccak.Compute(address.Bytes).Bytes.ToArray();
        HashSet<Hash256> expected = [with(requests.Length)];
        for (int i = 0; i < requests.Length; i++)
        {
            paths[i + 1] = Nibbles.EncodePath(requests[i].Path);
            expected.Add(requests[i].Hash);
        }
        return RequestExecutionAsync<IReadOnlyDictionary<Hash256, byte[]>>(AllocationContexts.Snap, async (peer, token) =>
        {
            peer.TryGetSatelliteProtocol<ISnapSyncPeer>(Protocol.Snap, out ISnapSyncPeer? snap);
            using GetTrieNodesRequest request = new()
            {
                RootHash = head.StateRoot,
                AccountAndStoragePaths = PathGroup.EncodeToRlpPathGroupList([new PathGroup { Group = paths }]),
            };
            using IByteArrayList response = await snap!.GetTrieNodes(request, token);
            if (response.Count == 0)
                throw new ExecutionDataUnavailableException("Execution peer does not have the requested storage trie nodes.");
            if (response.Count > requests.Length)
                throw new InvalidDataException("Execution peer returned too many storage trie nodes.");
            Dictionary<Hash256, byte[]> result = [with(response.Count)];
            for (int i = 0; i < response.Count; i++)
            {
                ReadOnlySpan<byte> bytes = response[i];
                if (bytes.Length is 0 or > ExecutionProofVerifier.MaxNodeBytes)
                    throw new InvalidDataException("Execution peer returned an oversized or empty storage trie node.");
                Hash256 hash = Keccak.Compute(bytes);
                if (!expected.Contains(hash) || !result.TryAdd(hash, bytes.ToArray()))
                    throw new InvalidDataException("Execution peer returned an unexpected or duplicate storage trie node.");
            }
            return result;
        }, cancellationToken, static peer => peer.CanGetTrieNodes(), hedge: false);
    }

    private async Task<HostedStorageSnapshot> DownloadStorageAsync(
        VerifiedHead head, Address address, Account account, CancellationToken cancellationToken)
    {
        PathWithAccount accountPath = new(ValueKeccak.Compute(address.Bytes), account.ToStruct());
        PeerInfo? soleSupplier = null;
        ISyncPeerPool pool = _services.Resolve<ISyncPeerPool>();
        try
        {
            return await DownloadFromAsync(null);
        }
        catch (InvalidDataException exception)
        {
            if (soleSupplier is not null)
                RecordPeerFailure(pool, AllocationContexts.Snap, soleSupplier, exception);
        }

        using ArrayPoolList<PeerInfo> candidates = new(pool.PeerCount);
        foreach (PeerInfo supplier in pool.InitializedPeers) candidates.Add(supplier);
        foreach (PeerInfo supplier in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsCooling(supplier, AllocationContexts.Snap)
                || !supplier.SyncPeer.TryGetSatelliteProtocol<ISnapSyncPeer>(Protocol.Snap, out _)) continue;
            try { return await DownloadFromAsync(supplier); }
            catch (InvalidDataException exception)
            {
                RecordPeerFailure(pool, AllocationContexts.Snap, supplier, exception);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && !_stop.IsCancellationRequested
                && exception is ExecutionDataUnavailableException or NotSupportedException or RpcException { Code: -32000 })
            {
                if (_logger.IsDebug) _logger.Debug($"Storage snapshot supplier is unavailable: {exception.Message}");
            }
        }
        throw new ExecutionDataUnavailableException("No execution peer supplied a complete authenticated storage snapshot.");

        Task<HostedStorageSnapshot> DownloadFromAsync(PeerInfo? pinned) =>
            HostedStorageDownload.DownloadAsync(account.StorageRoot, async (start, token) =>
            {
                (HostedStorageDownload.Page page, PeerInfo? supplier) = await RequestExecutionAsync(AllocationContexts.Snap,
                    async (peer, peerToken) =>
                    {
                        peer.TryGetSatelliteProtocol<ISnapSyncPeer>(Protocol.Snap, out ISnapSyncPeer? snap);
                        using StorageRange request = new()
                        {
                            RootHash = head.StateRoot,
                            Accounts = new ArrayPoolList<PathWithAccount>(1) { accountPath },
                            StartingHash = start,
                            LimitHash = ValueKeccak.MaxValue,
                        };
                        using SlotsAndProofs response = await snap!.GetStorageRange(request, peerToken);
                        HostedStorageDownload.Page verified = HostedStorageDownload.VerifyPage(accountPath, start, response, _logManager);
                        PeerInfo? current = pool.GetPeer(peer.Node);
                        return (verified, current is not null && ReferenceEquals(current.SyncPeer, peer) ? current : null);
                    }, token, peer => (pinned is null || ReferenceEquals(pinned.SyncPeer, peer))
                        && peer.TryGetSatelliteProtocol<ISnapSyncPeer>(Protocol.Snap, out _), hedge: false);
                if (pinned is null)
                {
                    // Only the first page can establish sole attribution; a later page can only invalidate it.
                    if (start == ValueKeccak.Zero) soleSupplier = supplier;
                    else if (!ReferenceEquals(soleSupplier, supplier)) soleSupplier = null;
                }
                return page;
            }, cancellationToken);
    }

    public async Task<ExecutionAccountProof> GetProofAsync(VerifiedHead head, Address address, UInt256[] storageKeys,
        CancellationToken cancellationToken)
    {
        ValueHash256 accountPath = ValueKeccak.Compute(address.Bytes);
        (Account account, byte[][] proof) = await RequestAsync(async (peer, token) =>
        {
            using AccountsAndProofs response = await peer.GetAccountRange(
                new AccountRange(head.StateRoot, accountPath, accountPath.IncrementPath()), token);
            return ExportAccountProof(head.StateRoot, address, response, _logManager);
        }, cancellationToken);
        ExecutionStorageProof[] storage = new ExecutionStorageProof[storageKeys.Length];
        if (storage.Length > 1 && account.HasStorage)
        {
            await Parallel.ForAsync(0, storage.Length, new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = ProofStorageConcurrency,
            }, FetchStorageProofAsync);
        }
        else
        {
            for (int i = 0; i < storage.Length; i++) await FetchStorageProofAsync(i, cancellationToken);
        }
        return new(account, proof, storage);

        async ValueTask FetchStorageProofAsync(int index, CancellationToken fetchToken)
        {
            UInt256 key = storageKeys[index];
            if (!account.HasStorage)
            {
                storage[index] = new(key, UInt256.Zero, []);
                return;
            }

            Span<byte> keyBytes = stackalloc byte[32];
            key.ToBigEndian(keyBytes);
            ValueHash256 slotPath = ValueKeccak.Compute(keyBytes);
            storage[index] = await RequestAsync(async (peer, token) =>
            {
                using StorageRange request = new()
                {
                    RootHash = head.StateRoot,
                    Accounts = new ArrayPoolList<PathWithAccount>(1) { new(accountPath, account.ToStruct()) },
                    StartingHash = slotPath,
                    LimitHash = slotPath.IncrementPath(),
                };
                using SlotsAndProofs response = await peer.GetStorageRange(request, token);
                return ExportStorageProof(account, accountPath, key, response, _logManager);
            }, fetchToken);
        }
    }

    internal static (Account Account, byte[][] Proof) ExportAccountProof(Hash256 stateRoot, Address address,
        AccountsAndProofs response, ILogManager logs)
    {
        ValueHash256 path = ValueKeccak.Compute(address.Bytes);
        ValidateAccountRange(response);
        if (response.PathAndAccounts.Count == 0 && response.Proofs.Count == 0 && stateRoot != Keccak.EmptyTreeHash)
            throw new ExecutionDataUnavailableException("Execution peer does not have the requested state root.");
        MemoryNodeStorage nodes = new();
        if (response.PathAndAccounts.Count != 0)
        {
            MemorySnapTrieFactory factory = new(nodes, logs);
            if (SnapProviderHelper.AddAccountRange(factory, 0, stateRoot, path, path.IncrementPath(),
                    response.PathAndAccounts, response.Proofs).result != AddRangeResult.OK)
                throw new InvalidDataException("Could not reconstruct the verified account path.");
        }
        byte[][] witness = CollectProof(nodes, stateRoot, path, response.Proofs);
        Account exported = ExecutionProofVerifier.VerifyAccount(stateRoot, address, witness);
        if (exported != FindAccount(path, response.PathAndAccounts))
            throw new InvalidDataException("Reconstructed account witness differs from the verified range.");
        return (exported, witness);
    }

    internal static ExecutionStorageProof ExportStorageProof(Account account, ValueHash256 accountPath, UInt256 key,
        SlotsAndProofs response, ILogManager logs)
    {
        ValueHash256 slotPath = ValueKeccak.Compute(key.ToBigEndian());
        ValidateStorageRange(response);
        MemoryNodeStorage nodes = new();
        bool hasSlots = response.PathsAndSlots.Count != 0 && response.PathsAndSlots[0].Count != 0;
        if (!hasSlots && response.Proofs.Count == 0 && account.StorageRoot != Keccak.EmptyTreeHash)
            throw new ExecutionDataUnavailableException("Execution peer does not have the requested storage root.");
        if (hasSlots)
        {
            MemorySnapTrieFactory factory = new(nodes, logs);
            if (SnapProviderHelper.ReconstructStorageProofNodes(factory, new(accountPath, account.ToStruct()),
                    response.PathsAndSlots[0], slotPath, slotPath.IncrementPath(), response.Proofs) != AddRangeResult.OK)
                throw new InvalidDataException("Could not reconstruct the verified storage path.");
        }
        byte[][] witness = CollectProof(nodes, account.StorageRoot, slotPath, response.Proofs);
        UInt256 value = ExecutionProofVerifier.VerifyStorage(account.StorageRoot, key, witness);
        if (value != (hasSlots ? FindStorage(slotPath, response.PathsAndSlots[0]) : UInt256.Zero))
            throw new InvalidDataException("Reconstructed storage witness differs from the verified range.");
        return new ExecutionStorageProof(key, value, witness);
    }

    private static byte[][] CollectProof(MemoryNodeStorage nodes, Hash256 root, ValueHash256 path, IByteArrayList rangeProof)
    {
        for (int i = 0; i < rangeProof.Count; i++) nodes.Set(null, TreePath.Empty, ValueKeccak.Compute(rangeProof[i]), rangeProof[i]);
        if (root == Keccak.EmptyTreeHash) return [];
        PatriciaTree tree = new(new RawScopedTrieStore(nodes), root, allowCommits: false, Nethermind.Logging.NullLogManager.Instance);
        ProofCollector collector = new(path.Bytes.ToArray());
        try
        {
            tree.Accept(collector, root);
            return collector.BuildResult();
        }
        catch (TrieException exception)
        {
            throw new InvalidDataException("An execution proof is invalid or incomplete.", exception);
        }
    }

    internal event Action<Transaction>? TransactionAnnounced;

    public Account GetPendingAccount(Address address, Account account, ulong nonceLimit = ulong.MaxValue) =>
        _transactionRelay.GetPendingAccount(address, account, nonceLimit);

    public async Task BroadcastTransactionAsync(Transaction transaction, CancellationToken cancellationToken)
    {
        _transactionRelay.AddVerified(transaction);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        ISyncPeerPool pool = _services.Resolve<ISyncPeerPool>();
        while (!deadline.IsCancellationRequested)
        {
            int sent = 0;
            foreach (PeerInfo peer in pool.InitializedPeers)
            {
                try { peer.SyncPeer.SendNewTransaction(transaction); sent++; }
                catch (Exception exception)
                {
                    if (_logger.IsDebug) _logger.Debug($"Transaction relay to {peer.SyncPeer.Node:c} failed: {exception.Message}");
                }
            }
            if (sent != 0)
            {
                if (_transactionRelay.TryMarkAnnounced(transaction.Hash!)) TransactionAnnounced?.Invoke(transaction);
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new RpcException(-32000, "No execution peer was available to relay the transaction.");
    }

    public async Task<BlockHeader> GetHeaderAsync(VerifiedHead head, CancellationToken cancellationToken)
    {
        BlockHeader header = await GetHeaderByHashAsync(head.BlockHash, cancellationToken);
        ValidateHead(header, head);
        return header;
    }

    private static void ValidateHead(BlockHeader header, VerifiedHead head)
    {
        if (header.Hash != head.BlockHash || header.Number != head.Number || header.StateRoot != head.StateRoot)
            throw new RpcException(-32000, "Execution header does not match the verified finalized payload.");
    }

    private static BlockHeader CopyHeader(BlockHeader header)
    {
        BlockHeader copy = header.Clone();
        copy.ExtraData = header.ExtraData.AsSpan().ToArray();
        copy.Bloom = header.Bloom is null ? null : header.Bloom.IsZeroLength ? Bloom.ZeroLength : new(header.Bloom.ReadOnlyBytes);
        copy.ParentHash = CopyHash(header.ParentHash);
        copy.UnclesHash = CopyHash(header.UnclesHash);
        copy.StateRoot = CopyHash(header.StateRoot);
        copy.TxRoot = CopyHash(header.TxRoot);
        copy.ReceiptsRoot = CopyHash(header.ReceiptsRoot);
        copy.MixHash = CopyHash(header.MixHash);
        copy.Hash = CopyHash(header.Hash);
        copy.WithdrawalsRoot = CopyHash(header.WithdrawalsRoot);
        copy.ParentBeaconBlockRoot = CopyHash(header.ParentBeaconBlockRoot);
        copy.RequestsHash = CopyHash(header.RequestsHash);
        copy.BlockAccessListHash = CopyHash(header.BlockAccessListHash);
        if (copy is AuRaBlockHeader { AuRaSignature: { } signature } aura)
            aura.AuRaSignature = signature.AsSpan().ToArray();
        return copy;
    }

    private static Hash256? CopyHash(Hash256? hash) => hash is null ? null : new Hash256(hash.ValueHash256);

    public async Task<ReadOnlyBlockAccessList> GetBlockAccessListAsync(BlockHeader header, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReleaseSpec spec = SpecProvider.GetSpec(header);
        if (!spec.BlockLevelAccessListsEnabled || !spec.IsEip6780Enabled || header.BlockAccessListHash is null)
            throw new NotSupportedException("BAL state updates are not enabled for this block's fork.");
        Hash256 hash = header.Hash ?? throw new InvalidDataException("BAL lookup requires an authenticated block hash.");
        BlockHeader snapshot = CopyHeader(header);
        return await _blockAccessLists.GetAsync(hash, stopping => RequestExecutionAsync(AllocationContexts.BlockAccessLists, async (peer, token) =>
        {
            if (peer.TryGetSatelliteProtocol<ISnapSyncPeer>(Protocol.Snap, out ISnapSyncPeer? snap) && snap.SnapProtocolVersion >= SnapVersions.Snap2)
            {
                using IByteArrayList response = await snap.GetBlockAccessLists([hash.ValueHash256], token);
                if (response.Count == 0 || response.Count == 1 && response[0].IsEmpty)
                    throw new ExecutionDataUnavailableException("Execution peer does not have the requested BAL.");
                if (response.Count != 1) throw new InvalidDataException("Execution peer returned an invalid BAL response size.");
                return VerifyBlockAccessList(snapshot, response[0]);
            }
            using IOwnedReadOnlyList<byte[]?> responseEth = await peer.GetBlockAccessLists([hash], token);
            if (responseEth.Count == 0 || responseEth.Count == 1 && responseEth[0] is null)
                throw new ExecutionDataUnavailableException("Execution peer does not have the requested BAL.");
            if (responseEth.Count != 1) throw new InvalidDataException("Execution peer returned an invalid BAL response size.");
            return VerifyBlockAccessList(snapshot, responseEth[0]!);
        }, stopping, static peer => peer.SupportsBlockAccessLists()
            || peer.TryGetSatelliteProtocol<ISnapSyncPeer>(Protocol.Snap, out ISnapSyncPeer? snap) && snap.SnapProtocolVersion >= SnapVersions.Snap2), cancellationToken);
    }

    internal static ReadOnlyBlockAccessList VerifyBlockAccessList(BlockHeader header, ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length is 0 or > 16 * 1024 * 1024 || header.BlockAccessListHash is null
            || Keccak.Compute(encoded) != header.BlockAccessListHash)
            throw new InvalidDataException("Execution peer returned a BAL inconsistent with the authenticated header.");
        ReadOnlyBlockAccessList list = BlockAccessListDecoder.Instance.DecodeCompleteNotNull(encoded);
        if ((ulong)list.ItemCount > header.GasLimit / Eip7928Constants.ItemCost)
            throw new InvalidDataException("Execution peer returned a BAL exceeding the block gas limit.");
        return list;
    }

    public async Task<BlockHeader> GetHeaderByHashAsync(Hash256 hash, CancellationToken cancellationToken)
    {
        Hash256 key = new(hash.ValueHash256);
        BlockHeader retained = await _headers.GetAsync(key,
            stopping => RequestHeaderAsync(key, _executionDataRequests, stopping), cancellationToken);
        return CopyHeader(retained);
    }

    internal async Task<BlockHeader> GetConsensusHeaderAsync(Hash256 hash, CancellationToken cancellationToken)
    {
        Hash256 key = new(hash.ValueHash256);
        // RPC work can occupy every shared execution slot while the publisher needs to advance.
        BlockHeader retained = await _headers.GetAsync(key, FetchAsync, cancellationToken, independentDownload: true);
        return CopyHeader(retained);

        async Task<BlockHeader> FetchAsync(CancellationToken stopping)
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(ConsensusHeaderTimeoutSeconds), _clock);
            using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, stopping);
            try { return await RequestHeaderAsync(key, _consensusHeaderRequests, attempt.Token); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !stopping.IsCancellationRequested)
            {
                throw new ExecutionDataUnavailableException("Timed out waiting for the execution header authenticated by consensus.");
            }
        }
    }

    private Task<BlockHeader> RequestHeaderAsync(Hash256 key, SemaphoreSlim requests, CancellationToken cancellationToken) =>
        RequestFromPeersAsync(AllocationContexts.Headers, requests, static peer => peer, async (peer, token) =>
        {
            BlockHeader header = await peer.GetHeadBlockHeader(new Hash256(key.ValueHash256), token)
                ?? throw new ExecutionDataUnavailableException("Execution peer does not have the requested header.");
            BlockHeader snapshot = CopyHeader(header);
            if (snapshot.Hash != key) throw new InvalidDataException("Execution peer returned a header inconsistent with the requested hash.");
            return snapshot;
        }, cancellationToken, canServe: null, failureMessage: "No execution peer supplied the requested execution header.");

    public async Task<Hash256[]> GetAncestorHashesAsync(VerifiedHead head, ulong firstNumber, CancellationToken cancellationToken)
    {
        if (firstNumber >= head.Number || head.Number - firstNumber > 256)
            throw new ArgumentOutOfRangeException(nameof(firstNumber));
        return await RequestExecutionAsync(AllocationContexts.Headers, async (peer, token) =>
        {
            Hash256[] hashes = new Hash256[checked((int)(head.Number - firstNumber + 1))];
            for (int offset = 0; offset < hashes.Length;)
            {
                int count = Math.Min(128, hashes.Length - offset);
                using IOwnedReadOnlyList<BlockHeader>? headers = await peer.GetBlockHeaders(firstNumber + (ulong)offset,
                    count, 0, token);
                if (headers is null || headers.Count == 0)
                    throw new ExecutionDataUnavailableException("Execution peer does not have the next ancestor header page.");
                if (headers.Count > count)
                    throw new InvalidDataException("Execution peer supplied too many ancestor headers.");
                for (int i = 0; i < headers.Count; i++)
                {
                    BlockHeader header = headers[i];
                    int index = offset + i;
                    if (header?.Hash is null)
                        throw new InvalidDataException("Execution peer returned an invalid ancestor header.");
                    LogHeaderChain.Verify(header, firstNumber + (ulong)index, header.Hash);
                    if (index > 0 && header.ParentHash != hashes[index - 1])
                    {
                        if (i == 0) throw new ExecutionDataUnavailableException("Execution peer changed its canonical chain between ancestor pages.");
                        throw new InvalidDataException("Execution peer returned an inconsistent ancestor header chain.");
                    }
                    hashes[index] = header.Hash;
                }
                offset += headers.Count;
            }
            if (hashes[^1] != head.BlockHash)
                throw new ExecutionDataUnavailableException("Execution peer's canonical ancestor chain does not reach the selected head.");
            return hashes;
        }, cancellationToken, failureMessage: AncestorChainUnavailable);
    }

    public async Task<BlockHeader> GetCanonicalHeaderAsync(VerifiedHead head, ulong number, CancellationToken cancellationToken)
    {
        if (number == head.Number) return await GetHeaderAsync(head, cancellationToken);
        if (number > head.Number || head.Number - number > 256) throw new ArgumentOutOfRangeException(nameof(number));
        cancellationToken.ThrowIfCancellationRequested();
        if (_headers.TryGetValue(head.BlockHash, out BlockHeader? anchor))
        {
            ValidateHead(anchor, head);
            return (await ReadLinkedHeadersAsync(anchor, number, number, cancellationToken, AncestorChainUnavailable))[0];
        }

        Hash256[] hashes = await GetAncestorHashesAsync(head, number, cancellationToken);
        BlockHeader header = await GetHeaderByHashAsync(hashes[0], cancellationToken);
        if (header.Number != number)
            throw new RpcException(-32000, "Execution peer returned a header outside the verified chain.");
        return header;
    }

    public Task<BlockHeader[]> GetRecentHeadersAsync(BlockHeader newest, int count, CancellationToken cancellationToken)
    {
        if (count is < 1 or > 257 || newest.Number < (ulong)(count - 1))
            throw new ArgumentOutOfRangeException(nameof(count));
        return ReadLinkedHeadersAsync(newest, newest.Number - (ulong)(count - 1), newest.Number,
            cancellationToken, AncestorChainUnavailable);
    }

    public Task<BlockHeader[]> GetLogHeadersAsync(VerifiedHead anchor, VerifiedHead finalized, ulong firstNumber,
        ulong lastNumber, CancellationToken cancellationToken)
    {
        lock (_historyLifetime)
        {
            ObjectDisposedException.ThrowIf(_historyStopped, this);
            cancellationToken.ThrowIfCancellationRequested();
        }
        return LogHeaderChain.GetAsync(this, anchor, finalized, firstNumber, lastNumber, cancellationToken,
            ReadFinalizedAsync, ReadTransientLogHeadersAsync);

        Task<BlockHeader[]> ReadFinalizedAsync(VerifiedHead head, ulong first, ulong last, CancellationToken token)
        {
            FinalizedHeaderHistory history;
            lock (_historyLifetime)
            {
                ObjectDisposedException.ThrowIf(_historyStopped, this);
                token.ThrowIfCancellationRequested();
                history = _logHistory.Value;
            }
            return history.GetHeadersAsync(head, first, last, token);
        }
    }

    private Task<BlockHeader[]> FetchLogHeaderPageAsync(BlockHeader cursor, int count, CancellationToken cancellationToken) =>
        FetchLinkedHeaderPageAsync(cursor, count, cancellationToken, BlockDataUnavailable);

    private Task<BlockHeader[]> FetchLinkedHeaderPageAsync(BlockHeader cursor, int count,
        CancellationToken cancellationToken, string failureMessage) =>
        RequestExecutionAsync(AllocationContexts.Headers, (peer, token) => ReadLogHeaderPageAsync(peer, cursor, count, token),
            cancellationToken, failureMessage: failureMessage);

    private FinalizedHeaderHistory.PageRequest[] BeginLogHeaderPages(ulong number, int count, CancellationToken cancellationToken)
    {
        if (count is < 1 or > FinalizedHeaderHistory.WireBatchSize || number < (ulong)count)
            throw new ArgumentOutOfRangeException(nameof(count));
        ulong first = number - (ulong)count;
        HeaderPrefetch group = new();
        Task<BlockHeader[]> probe = RequestExecutionAsync(AllocationContexts.Headers,
            (peer, token) => ReadLogHeaderBatchAsync(peer, first, count, token), cancellationToken,
            prefetch: group);
        ISyncPeerPool pool = _services.Resolve<ISyncPeerPool>();
        FinalizedHeaderHistory.PageRequest[] pages = new FinalizedHeaderHistory.PageRequest[
            (count + FinalizedHeaderHistory.PageSize - 1) / FinalizedHeaderHistory.PageSize];
        int remaining = count;
        for (int i = 0; i < pages.Length; i++)
        {
            int pageCount = Math.Min(FinalizedHeaderHistory.PageSize, remaining);
            int offset = remaining - pageCount;
            pages[i] = BeginPage(number, pageCount, offset);
            number -= (ulong)pageCount;
            remaining -= pageCount;
        }
        return pages;

        bool IsCurrent(PeerInfo peer) => peer.IsInitialized && ReferenceEquals(pool.GetPeer(peer.SyncPeer.Node), peer);

        FinalizedHeaderHistory.PageRequest BeginPage(ulong pageNumber, int pageCount, int offset)
        {
            HeaderPrefetch page = new();
            Task<BlockHeader[]> fetch = FetchAsync();
            return new(fetch, AuthenticateAsync);

            ReadOnlyMemory<BlockHeader> Prefix(ISyncPeer peer, BlockHeader[] batch)
            {
                int available = Math.Clamp(batch.Length - offset, 0, pageCount);
                return available != 0 && ReferenceEquals(peer, group.Supplier.SyncPeer) && IsCurrent(group.Supplier)
                    ? batch.AsMemory(offset, available) : default;
            }

            async Task<BlockHeader[]> FetchAsync()
            {
                BlockHeader[] batch = await probe;
                int available = Math.Clamp(batch.Length - offset, 0, pageCount);
                if (available == pageCount)
                {
                    page.Supplier = group.Supplier;
                    return batch.AsSpan(offset, pageCount).ToArray();
                }
                // The probe has released its permits before independent page continuations start.
                return await RequestExecutionAsync(AllocationContexts.Headers,
                    (peer, token) => ReadUnverifiedLogHeaderPageAsync(peer, pageNumber, pageCount, token, Prefix(peer, batch)),
                    cancellationToken, prefetch: page);
            }

            async Task<BlockHeader[]> ReadLeadingAsync(BlockHeader cursor, CancellationToken token)
            {
                BlockHeader[] batch = probe.IsCompletedSuccessfully ? await probe : [];
                return await RequestExecutionAsync(AllocationContexts.Headers, async (peer, cancellation) =>
                {
                    BlockHeader[] headers = await ReadUnverifiedLogHeaderPageAsync(peer, pageNumber, pageCount,
                        cancellation, Prefix(peer, batch));
                    VerifyLogHeaderPage(cursor, pageCount, headers);
                    return headers;
                }, token);
            }

            async Task<BlockHeader[]> AuthenticateAsync(BlockHeader cursor, CancellationToken token)
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30), _clock);
                using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token, timeout.Token);
                BlockHeader[] headers;
                try
                {
                    if (group.TryHandoff())
                    {
                        await ((Task)fetch).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                        deadline.Token.ThrowIfCancellationRequested();
                        return await ReadLeadingAsync(cursor, deadline.Token);
                    }
                    BlockHeader[] batch = await probe.WaitAsync(deadline.Token);
                    if (batch.Length < offset + pageCount && page.TryHandoff())
                    {
                        await ((Task)fetch).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                        deadline.Token.ThrowIfCancellationRequested();
                        return await ReadLeadingAsync(cursor, deadline.Token);
                    }
                    headers = await fetch.WaitAsync(deadline.Token);
                }
                catch (Exception exception) when (!deadline.IsCancellationRequested && (exception is RpcException or ExecutionDataUnavailableException
                    || exception is OperationCanceledException && (group.HandedOff || page.HandedOff)))
                {
                    await ((Task)fetch).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    deadline.Token.ThrowIfCancellationRequested();
                    return await ReadLeadingAsync(cursor, deadline.Token);
                }
                deadline.Token.ThrowIfCancellationRequested();
                PeerInfo supplier = page.Supplier;
                bool CanPrefer(PeerInfo peer) => IsCurrent(peer) && !IsCooling(peer, AllocationContexts.Headers);
                try { VerifyLogHeaderPage(cursor, pageCount, headers); }
                catch (ExecutionDataUnavailableException)
                {
                    return await ReadLeadingAsync(cursor, deadline.Token);
                }
                catch (InvalidDataException exception)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (IsCurrent(supplier)) RecordPeerFailure(pool, AllocationContexts.Headers, supplier, exception);
                    return await ReadLeadingAsync(cursor, deadline.Token);
                }
                deadline.Token.ThrowIfCancellationRequested();
                PromotePeer(AllocationContexts.Headers, supplier, CanPrefer);
                return headers;
            }
        }
    }

    private static async Task<BlockHeader[]> ReadLogHeaderBatchAsync(ISyncPeer peer, ulong first, int count,
        CancellationToken cancellationToken)
    {
        using IOwnedReadOnlyList<BlockHeader>? headers = await peer.GetBlockHeaders(first, count, 0, cancellationToken);
        if (headers is null || headers.Count == 0)
            throw new ExecutionDataUnavailableException("Execution peer does not have the requested log header page.");
        if (headers.Count > count) throw new InvalidDataException("Execution peer supplied too many log headers.");
        BlockHeader[] result = new BlockHeader[headers.Count];
        CopyLogHeaders(headers, result, first);
        return result;
    }

    internal static async Task<BlockHeader[]> ReadLogHeaderPageAsync(ISyncPeer peer, BlockHeader cursor, int count,
        CancellationToken cancellationToken)
    {
        BlockHeader[] result = await ReadUnverifiedLogHeaderPageAsync(peer, cursor.Number, count, cancellationToken);
        VerifyLogHeaderPage(cursor, count, result);
        return result;
    }

    private static void VerifyLogHeaderPage(BlockHeader cursor, int count, BlockHeader[] headers)
    {
        if (headers.Length != 0 && headers[^1]?.Hash is { } last && last != cursor.ParentHash)
        {
            // Numbered replies may follow another internally consistent canonical branch.
            BlockHeader branch = cursor.Clone();
            branch.ParentHash = last;
            FinalizedHeaderHistory.VerifyPage(branch, count, headers);
            throw new ExecutionDataUnavailableException("Execution peer's canonical log page does not reach the selected head.");
        }
        FinalizedHeaderHistory.VerifyPage(cursor, count, headers);
    }

    private static async Task<BlockHeader[]> ReadUnverifiedLogHeaderPageAsync(ISyncPeer peer, ulong number, int count,
        CancellationToken cancellationToken, ReadOnlyMemory<BlockHeader> prefix = default)
    {
        if (count is < 1 or > FinalizedHeaderHistory.PageSize || number < (ulong)count || prefix.Length > count)
            throw new InvalidDataException("Execution peer supplied an incomplete log header page.");
        BlockHeader[] result = new BlockHeader[count];
        ulong first = number - (ulong)count;
        prefix.Span.CopyTo(result);
        int received = prefix.Length;
        while (received < count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int requested = count - received;
            using IOwnedReadOnlyList<BlockHeader>? headers = await peer.GetBlockHeaders(first + (ulong)received, requested, 0, cancellationToken);
            if (headers is null || headers.Count == 0)
                throw new ExecutionDataUnavailableException("Execution peer does not have the requested log header page.");
            if (headers.Count > requested) throw new InvalidDataException("Execution peer supplied too many log headers.");
            CopyLogHeaders(headers, result.AsSpan(received), first + (ulong)received);
            if (received > 0 && result[received].ParentHash != result[received - 1].Hash)
            {
                VerifySegment(result.AsSpan(0, received), first);
                VerifySegment(result.AsSpan(received, headers.Count), first + (ulong)received);
                throw new ExecutionDataUnavailableException("Execution peer changed its canonical chain between log header pages.");
            }
            received += headers.Count;
        }
        return result;

        static void VerifySegment(ReadOnlySpan<BlockHeader> segment, ulong firstNumber)
        {
            for (int i = 0; i < segment.Length; i++)
            {
                BlockHeader header = segment[i];
                if (header?.Hash is not { } hash) throw new InvalidDataException("Execution peer supplied an invalid log header.");
                LogHeaderChain.Verify(header, firstNumber + (ulong)i, hash);
                if (i > 0 && header.ParentHash != segment[i - 1].Hash)
                    throw new InvalidDataException("Execution peer supplied an inconsistent log header segment.");
            }
        }
    }

    private static void CopyLogHeaders(IOwnedReadOnlyList<BlockHeader> headers, Span<BlockHeader> result, ulong first)
    {
        IRlpDecoder<BlockHeader> decoder = Rlp.GetDecoderOrThrow<BlockHeader>();
        for (int i = 0; i < headers.Count; i++)
        {
            BlockHeader header = headers[i];
            if (header is null || header.Number != first + (ulong)i
                || decoder.GetLength(header, RlpBehaviors.None) > FinalizedHeaderHistory.MaxHeaderBytes)
                throw new InvalidDataException("Execution peer supplied an invalid log header page.");
            result[i] = header;
        }
    }

    private async Task<BlockHeader[]> ReadTransientLogHeadersAsync(VerifiedHead anchor, ulong first, ulong last,
        CancellationToken cancellationToken)
    {
        BlockHeader cursor = await GetHeaderAsync(anchor, cancellationToken);
        LogHeaderChain.Verify(cursor, anchor.Number, anchor.BlockHash);
        if (cursor.StateRoot != anchor.StateRoot)
            throw new InvalidDataException("Optimistic log anchor has an inconsistent state root.");
        return await ReadLinkedHeadersAsync(cursor, first, last, cancellationToken, BlockDataUnavailable);
    }

    private async Task<BlockHeader[]> ReadLinkedHeadersAsync(BlockHeader cursor, ulong first, ulong last,
        CancellationToken cancellationToken, string failureMessage)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BlockHeader[] result = new BlockHeader[checked((int)(last - first + 1))];
        if (cursor.Number <= last) result[checked((int)(cursor.Number - first))] = cursor;
        while (cursor.Number > first)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cursor.ParentHash is { } parentHash && _headers.TryGetValue(parentHash, out BlockHeader? parent)
                && parent.Number == cursor.Number - 1)
            {
                cursor = parent;
                if (cursor.Number <= last) result[checked((int)(cursor.Number - first))] = CopyHeader(cursor);
                continue;
            }

            int count = (int)Math.Min(FinalizedHeaderHistory.PageSize, cursor.Number - first);
            BlockHeader[] page = await FetchLinkedHeaderPageAsync(cursor, count, cancellationToken, failureMessage);
            foreach (BlockHeader header in page)
            {
                BlockHeader retained = CopyHeader(header);
                _headers.SetVerified(retained.Hash!, retained);
                if (header.Number <= last) result[checked((int)(header.Number - first))] = header;
            }
            cursor = page[0];
        }
        return result;
    }

    public Task<Block> GetBlockAsync(BlockHeader header, CancellationToken cancellationToken)
    {
        if (!header.HasBody)
        {
            BlockBody empty = new([], [], header.WithdrawalsRoot is null ? null : []);
            VerifyBlockBody(header, empty);
            return Task.FromResult(new Block(header, empty));
        }
        return _bodies.GetAsync(header.Hash!, stopping => RequestExecutionAsync(AllocationContexts.Bodies, async (peer, token) =>
        {
            using OwnedBlockBodies bodies = await peer.GetBlockBodies([header.Hash!], token);
            if (bodies.Count == 0 || bodies.Count == 1 && bodies[0] is null)
                throw new ExecutionDataUnavailableException("Execution peer does not have the requested block body.");
            if (bodies.Count != 1) throw new InvalidDataException("Execution peer returned an invalid block body response size.");
            BlockBody body = bodies[0]!;
            VerifyBlockBody(header, body);
            bodies.Disown();
            return new Block(header, body);
        }, stopping), cancellationToken);
    }

    internal static void VerifyBlockBody(BlockHeader header, BlockBody body)
    {
        if (!BlockValidator.ValidateTxRootMatchesTxs(header, body, out _)
            || !BlockValidator.ValidateUnclesHashMatches(header, body, out _)
            || !BlockValidator.ValidateWithdrawalsHashMatches(header, body, out _))
            throw new InvalidDataException("Execution peer returned a block body inconsistent with the verified header.");
    }

    public Task<TxReceipt[]> GetReceiptsAsync(BlockHeader header, CancellationToken cancellationToken, int? transactionCount = null) =>
        GetReceiptTasks([header], cancellationToken, transactionCount)[0];

    public Task<TxReceipt[]>[] GetReceiptsAsync(BlockHeader[] headers, CancellationToken cancellationToken) =>
        GetReceiptTasks(headers, cancellationToken, null);

    private Task<TxReceipt[]>[] GetReceiptTasks(BlockHeader[] headers, CancellationToken cancellationToken, int? transactionCount)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(headers.Length, PayloadCache<TxReceipt[]>.MaxDownloads);
        BlockHeader[] requestHeaders = new BlockHeader[headers.Length];
        int?[] maximums = new int?[headers.Length];
        Task<TxReceipt[]>[] result = new Task<TxReceipt[]>[headers.Length];
        using ArrayPoolList<Hash256> hashes = new(headers.Length);
        using ArrayPoolList<int> indices = new(headers.Length);
        for (int i = 0; i < headers.Length; i++)
        {
            BlockHeader header = requestHeaders[i] = headers[i].Clone();
            if (transactionCount == 0 || header.ReceiptsRoot == Keccak.EmptyTreeHash)
            {
                VerifyReceipts(header, [], SpecProvider);
                if (transactionCount is > 0) throw new InvalidDataException("Verified receipt count does not match the block transaction count.");
                result[i] = Task.FromResult(Array.Empty<TxReceipt>());
            }
            else
            {
                maximums[i] = transactionCount ?? GetReceiptCountLimit(header, SpecProvider.GetSpec(header));
                hashes.Add(header.Hash!);
                indices.Add(i);
            }
        }
        if (hashes.Count == 0) return result;
        Task<TxReceipt[]>[] pending = _receipts.GetManyAsync(hashes.AsSpan().ToArray(), FetchAsync, cancellationToken);
        for (int i = 0; i < pending.Length; i++)
            result[indices[i]] = CopyAsync(maximums[indices[i]], pending[i]);
        return result;

        Task<TxReceipt[]>[] FetchAsync(Hash256[] ownedHashes, CancellationToken stopping)
        {
            TaskCompletionSource<TxReceipt[]>[] completed = new TaskCompletionSource<TxReceipt[]>[ownedHashes.Length - 1];
            Task<TxReceipt[]>[] tasks = new Task<TxReceipt[]>[ownedHashes.Length];
            for (int i = 0; i < completed.Length; i++)
            {
                completed[i] = new(TaskCreationOptions.RunContinuationsAsynchronously);
                tasks[i] = completed[i].Task;
            }
            tasks[^1] = LastAsync();
            return tasks;

            async Task<TxReceipt[]> LastAsync()
            {
                try
                {
                    BlockHeader[] ownedHeaders = new BlockHeader[ownedHashes.Length];
                    int[] counts = new int[ownedHashes.Length];
                    int[] limits = new int[ownedHashes.Length];
                    for (int i = 0; i < ownedHashes.Length; i++)
                    {
                        int sourceIndex = 0;
                        while (requestHeaders[sourceIndex].Hash != ownedHashes[i]) sourceIndex++;
                        BlockHeader header = ownedHeaders[i] = requestHeaders[sourceIndex];
                        int? maximum = maximums[sourceIndex];
                        int? exact = transactionCount;
                        maximum ??= exact = (await GetBlockAsync(header, stopping)).Transactions.Length;
                        limits[i] = maximum.Value;
                        counts[i] = exact ?? -1;
                    }
                    int offset = 0;
                    while (true)
                    {
                        TxReceipt[][] prefix = await ReadPrefixAsync(offset);
                        for (int i = 0; i < prefix.Length; i++)
                        {
                            int index = offset + i;
                            if (index == tasks.Length - 1) return prefix[i];
                            completed[index].TrySetResult(prefix[i]);
                        }
                        offset += prefix.Length;
                    }

                    async Task<TxReceipt[][]> ReadPrefixAsync(int start)
                    {
                        Lock registration = new();
                        bool registered = false;
                        try
                        {
                            return await RequestExecutionAsync(AllocationContexts.Receipts, async (peer, token) =>
                            {
                                lock (registration)
                                {
                                    if (!registered)
                                    {
                                        for (int i = start; i < ownedHeaders.Length; i++) _syncServer.AddReceiptHeader(ownedHeaders[i]);
                                        registered = true;
                                    }
                                }
                                int requestedLength = peer.ProtocolVersion < 70 ? 1 : ownedHashes.Length - start;
                                using ArrayPoolList<Hash256> remaining = new(requestedLength);
                                for (int i = start; i < start + requestedLength; i++)
                                    remaining.Add(ownedHashes[i]);
                                using IOwnedReadOnlyList<TxReceipt[]?> batches = await peer.GetReceipts(remaining,
                                    limits.AsMemory(start, requestedLength), token);
                                if (batches.Count == 0)
                                    throw new ExecutionDataUnavailableException("Execution peer does not have the requested receipt list.");
                                if (batches.Count > remaining.Count)
                                    throw new InvalidDataException("Execution peer returned too many receipt lists.");
                                TxReceipt[][] receipts = new TxReceipt[batches.Count][];
                                for (int i = 0; i < batches.Count; i++)
                                {
                                    int index = start + i;
                                    if (batches[i] is not { } batch)
                                        throw new ExecutionDataUnavailableException("Execution peer does not have the requested receipt list.");
                                    if (batch.Length > limits[index] ||
                                        counts[index] >= 0 && batch.Length != counts[index])
                                        throw new InvalidDataException("Execution peer did not supply the complete receipt list.");
                                    VerifyReceipts(ownedHeaders[index], batch, SpecProvider);
                                    receipts[i] = CopyReceipts(batch);
                                }
                                return receipts;
                            }, stopping);
                        }
                        finally
                        {
                            if (registered)
                                for (int i = start; i < ownedHeaders.Length; i++) _syncServer.RemoveReceiptHeader(ownedHeaders[i]);
                        }
                    }
                }
                catch (Exception exception)
                {
                    foreach (TaskCompletionSource<TxReceipt[]> completion in completed) completion.TrySetException(exception);
                    throw;
                }
            }
        }

        async Task<TxReceipt[]> CopyAsync(int? maximum, Task<TxReceipt[]> pendingReceipts)
        {
            TxReceipt[] cached = await pendingReceipts;
            if (maximum is { } limit && cached.Length > limit || transactionCount is { } count && cached.Length != count)
                throw new InvalidDataException("Verified receipt count does not match the block transaction count.");
            return CopyReceipts(cached);
        }
    }

    private static TxReceipt[] CopyReceipts(TxReceipt[] receipts)
    {
        TxReceipt[] copy = new TxReceipt[receipts.Length];
        for (int i = 0; i < copy.Length; i++) copy[i] = new TxReceipt(receipts[i]);
        return copy;
    }

    internal static int? GetReceiptCountLimit(BlockHeader header, IReleaseSpec spec)
    {
        ulong minimumGas = spec.IsEip2780Enabled ? GasCostOf.TransactionEip2780 : GasCostOf.Transaction;
        if (spec.IsEip8141Enabled) minimumGas = Math.Min(minimumGas, (ulong)Eip8141Constants.IntrinsicGasCost);
        // Block accounting uses gross gas under EIP-7778, otherwise the fork's maximum refund applies.
        if (!spec.IsEip7778Enabled) minimumGas -= RefundHelper.CalculateClaimableRefund(minimumGas, minimumGas, spec);
        return minimumGas == 0 ? null : (int)Math.Min(header.GasLimit / minimumGas, (ulong)BlockBodyDecoder.TransactionsCountLimit.Limit);
    }

    private static long BodySize(Block block)
    {
        long size = 2048 + block.Uncles.LongLength * 512 + (block.Withdrawals?.LongLength ?? 0) * 128;
        foreach (Transaction transaction in block.Transactions)
            size += 1024 + 4L * TxDecoder.Instance.GetLength(transaction, RlpBehaviors.SkipTypedWrapping);
        return size;
    }

    private static long ReceiptsSize(TxReceipt[] receipts)
    {
        long size = 1024;
        foreach (TxReceipt receipt in receipts)
        {
            size += 512 + (receipt.FrameReceipts?.LongLength ?? 0) * 128;
            foreach (LogEntry log in receipt.Logs ?? [])
                size += 128 + log.Data.LongLength + log.Topics.LongLength * 64;
        }
        return size;
    }

    internal static void VerifyReceipts(BlockHeader header, TxReceipt[] receipts, ISpecProvider specProvider)
    {
        IReleaseSpec spec = specProvider.GetSpec(header);
        if (ReceiptTrie.CalculateRoot(spec, receipts, new ReceiptMessageDecoder()) != header.ReceiptsRoot)
            throw new InvalidDataException("Execution peer returned receipts inconsistent with the verified header.");
        foreach (TxReceipt receipt in receipts)
        {
            if (spec.IsEip658Enabled && receipt.PostTransactionState is not null)
                throw new InvalidDataException("Execution peer returned a post-state root for a status receipt.");
            if (spec.IsEip7668Enabled) receipt.Bloom = new Bloom(receipt.Logs);
        }
    }

    private Task<T> RequestExecutionAsync<T>(AllocationContexts context, Func<ISyncPeer, CancellationToken, Task<T>> request,
        CancellationToken cancellationToken, Func<ISyncPeer, bool>? canServe = null,
        string failureMessage = BlockDataUnavailable, HeaderPrefetch? prefetch = null,
        bool hedge = true) =>
        RequestFromPeersAsync(context, context == AllocationContexts.Snap ? _snapRequests : _executionDataRequests,
            static peer => peer, request, cancellationToken, canServe, failureMessage,
            prefetch: prefetch, hedge: hedge);

    private Task<T> RequestAsync<T>(Func<ISnapSyncPeer, CancellationToken, Task<T>> request, CancellationToken cancellationToken,
        string operation = "proof") =>
        RequestFromPeersAsync(AllocationContexts.Snap, _snapRequests,
            static peer => peer.TryGetSatelliteProtocol<ISnapSyncPeer>(Protocol.Snap, out ISnapSyncPeer? snap) ? snap : null,
            request, cancellationToken, null, "No execution peer supplied a valid SNAP proof for the selected verified state.", operation);

    private async Task<T> RequestFromPeersAsync<TPeer, T>(AllocationContexts context, SemaphoreSlim requests, Func<ISyncPeer, TPeer?> selectPeer,
        Func<TPeer, CancellationToken, Task<T>> request, CancellationToken cancellationToken,
        Func<ISyncPeer, bool>? canServe, string failureMessage, string? operation = null,
        HeaderPrefetch? prefetch = null, bool hedge = true) where TPeer : class
    {
        int maxParallelSuppliers = hedge ? 2 : 1;
        bool trace = _logger.IsDebug;
        using Activity? diagnostic = trace
            ? new Activity("LightClientPeerRequest").SetIdFormat(ActivityIdFormat.W3C).Start() : null;
        if (trace)
            _logger.Debug($"LC peer id={diagnostic?.Id} parent={diagnostic?.ParentId} context={context} operation={operation ?? context.ToString()}");
        // Speculation must leave the header lane available until its trusted cursor arrives.
        bool bulk = context is not (AllocationContexts.Headers or AllocationContexts.Snap) || prefetch is not null;
        bool bulkAcquired = false;
        bool requestAcquired = false;
        long admissionStart = trace ? _clock.GetTimestamp() : 0;
        long requestSlotStart = admissionStart;
        bool requestSlotStarted = !bulk;
        CancellationToken stopToken = _stop.Token;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30), _clock);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopToken, timeout.Token);
        try
        {
            if (bulk)
            {
                await (prefetch is null
                    ? _executionBulkRequests.WaitAsync(deadline.Token)
                    : WaitForPrefetchSlotAsync(_executionBulkRequests, prefetch, deadline));
                bulkAcquired = true;
                if (trace)
                    _logger.Debug($"LC peer id={diagnostic?.Id} phase=bulk-admission admitted=True elapsedMs={_clock.GetElapsedTime(admissionStart).TotalMilliseconds:F3}");
                requestSlotStart = trace ? _clock.GetTimestamp() : 0;
                requestSlotStarted = true;
            }
            await (prefetch is null
                ? requests.WaitAsync(deadline.Token)
                : WaitForPrefetchSlotAsync(requests, prefetch, deadline));
            requestAcquired = true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !stopToken.IsCancellationRequested
            && prefetch?.HandedOff is not true)
        {
            string kind = context == AllocationContexts.Snap ? "SNAP" : "execution data";
            throw new RpcException(-32000, $"Timed out waiting for an available {kind} request slot.");
        }
        finally
        {
            if (!requestAcquired && bulkAcquired) _executionBulkRequests.Release();
            if (trace)
                _logger.Debug($"LC peer id={diagnostic?.Id} phase=admission admitted={requestAcquired} bulkAdmitted={bulkAcquired} elapsedMs={_clock.GetElapsedTime(admissionStart).TotalMilliseconds:F3} requestSlotMs={(requestSlotStarted ? _clock.GetElapsedTime(requestSlotStart).TotalMilliseconds : 0):F3}");
        }
        try
        {
            if (prefetch is not null && !prefetch.TryAdmit())
            {
                await deadline.CancelAsync();
                throw new OperationCanceledException(deadline.Token);
            }
            ISyncPeerPool pool = _services.Resolve<ISyncPeerPool>();
            bool IsCurrent(PeerInfo peer) => peer.IsInitialized && ReferenceEquals(pool.GetPeer(peer.SyncPeer.Node), peer);
            bool CanPrefer(PeerInfo peer) => IsCurrent(peer) && (canServe is null || canServe(peer.SyncPeer))
                && selectPeer(peer.SyncPeer) is not null && !IsCooling(peer, context);
            while (!deadline.IsCancellationRequested)
            {
                bool hasCapablePeer = false;
                _preferredPeers.TryGetValue(context, out PeerInfo? preferred);
                _lastPeers.TryGetValue(context, out PeerInfo? last);
                using ArrayPoolList<PeerInfo> peers = new(pool.PeerCount);
                using ArrayPoolList<(PeerInfo Peer, TPeer Protocol)> eligible = new(pool.PeerCount);
                foreach (PeerInfo peer in pool.InitializedPeers) peers.Add(peer);
                int start = 0;
                int preferredIndex = -1;
                for (int i = 0; i < peers.Count; i++)
                {
                    if (ReferenceEquals(peers[i], last)) start = (i + 1) % peers.Count;
                    if (ReferenceEquals(peers[i], preferred)) preferredIndex = i;
                }
                for (int offset = preferredIndex >= 0 ? -1 : 0; offset < peers.Count; offset++)
                {
                    int index = offset < 0 ? preferredIndex : (start + offset) % peers.Count;
                    if (offset >= 0 && index == preferredIndex) continue;
                    PeerInfo peer = peers[index];
                    if (deadline.IsCancellationRequested) break;
                    if (!IsCurrent(peer)) continue;
                    if (canServe is not null && !canServe(peer.SyncPeer)) continue;
                    TPeer? protocolPeer = selectPeer(peer.SyncPeer);
                    if (protocolPeer is null) continue;
                    hasCapablePeer = true;
                    if (IsCooling(peer, context)) continue;
                    eligible.Add((peer, protocolPeer));
                }
                if (canServe is not null && !hasCapablePeer && !deadline.IsCancellationRequested)
                    throw new NotSupportedException("No execution peer supports the requested data protocol.");
                using CancellationTokenSource round = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                List<(PeerInfo Peer, Task<(T Result, Exception? Error)> Request)> pending = [with(maxParallelSuppliers)];
                Task? hedgeDelay = null;
                ExecutionDataUnavailableException? unavailable = null;
                bool selected = false;
                int next = 0;
                try
                {
                    while ((next < eligible.Count || pending.Count != 0) && !deadline.IsCancellationRequested)
                    {
                        do
                        {
                            if (next == eligible.Count || pending.Count == maxParallelSuppliers) break;
                            if (bulk && pending.Count == 1 && hedgeDelay is not { IsCompletedSuccessfully: true })
                            {
                                // Large responses can arrive from both suppliers before the losing request is canceled.
                                Task primary = pending[0].Request;
                                hedgeDelay ??= Task.Delay(BulkHedgeDelay, _clock, round.Token);
                                await Task.WhenAny(primary, hedgeDelay);
                                if (primary.IsCompleted || round.IsCancellationRequested) break;
                            }
                            (PeerInfo peer, TPeer protocol) = eligible[next++];
                            if (!CanPrefer(peer)) continue;
                            _lastPeers[context] = peer;
                            pending.Add((peer, RequestPeerAsync(peer, protocol, round.Token)));
                        }
                        while (pending.Count == 0 || !pending[0].Request.IsCompleted);
                        if (pending.Count == 0) continue;
                        Task<(T Result, Exception? Error)> completed = pending.Count == 1 ? pending[0].Request
                            : await Task.WhenAny(pending[0].Request, pending[1].Request);
                        int completedIndex = ReferenceEquals(completed, pending[0].Request) ? 0 : 1;
                        PeerInfo supplier = pending[completedIndex].Peer;
                        pending.RemoveAt(completedIndex);
                        (T result, Exception? error) = await completed;
                        if (deadline.IsCancellationRequested) break;
                        if (error is null)
                        {
                            if (trace)
                                _logger.Debug($"LC peer id={diagnostic?.Id} phase=selection peer={supplier.SyncPeer.Node:c} outcome={(prefetch is null ? "selected" : "received-unverified")}");
                            if (prefetch is null) PromotePeer(context, supplier, CanPrefer);
                            else prefetch.Supplier = supplier;
                            selected = true;
                            return result;
                        }
                        if (error is ConcurrencyLimitReachedException)
                        {
                            if (_logger.IsDebug) _logger.Debug($"Execution {context} peer {supplier.SyncPeer.Node:c} is busy: {error.Message}");
                            continue;
                        }
                        if (error is ExecutionDataUnavailableException missing)
                        {
                            unavailable = missing;
                            continue;
                        }
                        RecordPeerFailure(pool, context, supplier, error);
                    }
                }
                finally
                {
                    await round.CancelAsync();
                    foreach ((PeerInfo losingPeer, Task<(T Result, Exception? Error)> pendingRequest) in pending)
                    {
                        (_, Exception? error) = await pendingRequest;
                        if (prefetch is null && selected && ReferenceEquals(losingPeer, preferred)
                            && error is OperationCanceledException && IsCurrent(losingPeer)
                            && !deadline.IsCancellationRequested)
                        {
                            int losses = CountRecentSample(_peerPerformance.GetValue(losingPeer, static _ => new()).HedgeLosses, context);
                            if (losses >= 3)
                                _preferredPeers.TryRemove(new KeyValuePair<AllocationContexts, PeerInfo>(context, losingPeer));
                        }
                        else if (selected && error is not (null or OperationCanceledException or ExecutionDataUnavailableException or ConcurrencyLimitReachedException)
                            && !deadline.IsCancellationRequested) RecordPeerFailure(pool, context, losingPeer, error);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    stopToken.ThrowIfCancellationRequested();
                    if (timeout.IsCancellationRequested) throw new RpcException(-32000, failureMessage);
                }
                if (unavailable is not null) throw unavailable;
                await Task.Delay(TimeSpan.FromSeconds(1), _clock, deadline.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            cancellationToken.ThrowIfCancellationRequested();
            stopToken.ThrowIfCancellationRequested();
            throw new RpcException(-32000, failureMessage);

            async Task<(T Result, Exception? Error)> RequestPeerAsync(PeerInfo info, TPeer peer, CancellationToken roundToken)
            {
                using Activity? attemptDiagnostic = trace
                    ? new Activity("LightClientPeerAttempt").SetIdFormat(ActivityIdFormat.W3C).Start() : null;
                long attemptStart = _clock.GetTimestamp();
                string outcome = "failed";
                if (trace)
                    _logger.Debug($"LC peer id={attemptDiagnostic?.Id} parent={attemptDiagnostic?.ParentId} phase=attempt-start peer={info.SyncPeer.Node:c} context={context}");
                try
                {
                    using CancellationTokenSource peerTimeout = new(TimeSpan.FromSeconds(10), _clock);
                    using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(roundToken, peerTimeout.Token);
                    T result = await request(peer, attempt.Token);
                    attempt.Token.ThrowIfCancellationRequested();
                    if (prefetch is null)
                    {
                        TimeSpan elapsed = _clock.GetElapsedTime(attemptStart);
                        _peerStats.ReportTransferSpeedEvent(info.SyncPeer.Node, TransferSpeedType.Latency, (long)elapsed.TotalMilliseconds);
                        PeerPerformance performance = _peerPerformance.GetValue(info, static _ => new());
                        performance.Latencies[context] = (long)elapsed.TotalMilliseconds;
                        performance.HedgeLosses.TryRemove(context, out _);
                        if (context == AllocationContexts.Headers && result is BlockHeader
                            || context == AllocationContexts.Snap && operation is not null && result is not byte[])
                        {
                            if (elapsed >= TimeSpan.FromMilliseconds(SlowResponseMilliseconds))
                            {
                                if (CountRecentSample(performance.SlowResponses, context) >= 3)
                                    _preferredPeers.TryRemove(new KeyValuePair<AllocationContexts, PeerInfo>(context, info));
                            }
                            else performance.SlowResponses.TryRemove(context, out _);
                        }
                    }
                    outcome = "completed";
                    return (result, null);
                }
                catch (Exception exception)
                {
                    outcome = roundToken.IsCancellationRequested ? "interrupted"
                        : exception is ConcurrencyLimitReachedException ? "busy" : "failed";
                    return (default!, exception);
                }
                finally
                {
                    if (trace)
                        _logger.Debug($"LC peer id={attemptDiagnostic?.Id} parent={attemptDiagnostic?.ParentId} phase=peer-attempt peer={info.SyncPeer.Node:c} context={context} outcome={outcome} inclusiveMs={_clock.GetElapsedTime(attemptStart).TotalMilliseconds:F3}");
                }
            }
        }
        finally
        {
            requests.Release();
            if (bulkAcquired) _executionBulkRequests.Release();
        }
    }

    private sealed class HeaderPrefetch
    {
        private const int Admitted = 1;
        private const int Handoff = 2;
        private int _state;
        internal readonly TaskCompletionSource Leading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal PeerInfo Supplier = null!;
        internal bool HandedOff => Volatile.Read(ref _state) == Handoff;
        internal bool TryAdmit() => Interlocked.CompareExchange(ref _state, Admitted, 0) == 0;

        internal bool TryHandoff()
        {
            if (Interlocked.CompareExchange(ref _state, Handoff, 0) != 0) return false;
            Leading.TrySetResult();
            return true;
        }
    }

    private static async Task WaitForPrefetchSlotAsync(SemaphoreSlim slots, HeaderPrefetch prefetch,
        CancellationTokenSource deadline)
    {
        Task waiting = slots.WaitAsync(deadline.Token);
        if (!waiting.IsCompleted)
        {
            await Task.WhenAny(waiting, prefetch.Leading.Task);
            if (prefetch.HandedOff) await deadline.CancelAsync();
        }
        await waiting;
    }

    private void PromotePeer(AllocationContexts context, PeerInfo supplier, Func<PeerInfo, bool> canPrefer)
    {
        while (canPrefer(supplier) && !IsSlow(supplier, context))
        {
            if (_preferredPeers.TryAdd(context, supplier)) break;
            if (_preferredPeers.TryGetValue(context, out PeerInfo? current)
                && (canPrefer(current) && !IsSlow(current, context) && PeerLatency(current, context) <= PeerLatency(supplier, context)
                    || _preferredPeers.TryUpdate(context, supplier, current))) break;
        }
    }

    private sealed class PeerPerformance
    {
        internal readonly ConcurrentDictionary<AllocationContexts, (long Timestamp, int Count)> HedgeLosses = new();
        internal readonly ConcurrentDictionary<AllocationContexts, (long Timestamp, int Count)> SlowResponses = new();
        internal readonly ConcurrentDictionary<AllocationContexts, long> Latencies = new();
    }

    private int CountRecentSample(ConcurrentDictionary<AllocationContexts, (long Timestamp, int Count)> samples, AllocationContexts context)
    {
        long now = _clock.GetTimestamp();
        return samples.AddOrUpdate(context, (now, 1), (_, previous) =>
            _clock.GetElapsedTime(previous.Timestamp, now) < TimeSpan.FromMinutes(1)
                ? (previous.Timestamp, Math.Min(3, previous.Count + 1)) : (now, 1)).Count;
    }

    private bool IsSlow(PeerInfo peer, AllocationContexts context) => _peerPerformance.TryGetValue(peer, out PeerPerformance? performance)
        && performance.SlowResponses.TryGetValue(context, out (long Timestamp, int Count) sample)
        && sample.Count >= 3 && _clock.GetElapsedTime(sample.Timestamp) < TimeSpan.FromMinutes(1);

    private long PeerLatency(PeerInfo peer, AllocationContexts context) => _peerPerformance.TryGetValue(peer, out PeerPerformance? performance)
        && performance.Latencies.TryGetValue(context, out long latency) ? latency : long.MaxValue;

    private void RecordPeerFailure(ISyncPeerPool pool, AllocationContexts context, PeerInfo supplier, Exception error)
    {
        if (!ReferenceEquals(pool.GetPeer(supplier.SyncPeer.Node), supplier)) return;
        bool invalid = error is InvalidDataException or RlpReaderException or TrieException;
        CoolPeer(context, supplier, invalid ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(15));
        pool.ReportWeakPeer(supplier, context);
        if (invalid) supplier.SyncPeer.Disconnect(DisconnectReason.BreachOfProtocol, "Execution data failed verification");
        if (_logger.IsWarn)
        {
            string detail = $"{error.GetType().Name}: {error.Message}";
            _logger.Warn(context == AllocationContexts.Snap
                ? $"SNAP peer {supplier.SyncPeer.Node:c} could not serve the selected state: {detail}"
                : $"Execution {context} peer {supplier.SyncPeer.Node:c} failed: {detail}");
        }
    }

    private void CoolPeer(AllocationContexts context, PeerInfo supplier, TimeSpan duration)
    {
        ISyncPeerPool pool = _services.Resolve<ISyncPeerPool>();
        lock (_cooldownLock)
        {
            foreach (object key in _peerCooldowns.Keys)
            {
                if (!_peerCooldowns.TryGetValue(key, out (long Timestamp, TimeSpan Duration, WeakReference<PeerInfo> Owner) value)) continue;
                if (!value.Owner.TryGetTarget(out PeerInfo? peer) || !ReferenceEquals(pool.GetPeer(peer.SyncPeer.Node), peer)
                    || _clock.GetElapsedTime(value.Timestamp) >= value.Duration) _peerCooldowns.Remove(key);
            }
            if (_peerCooldowns.Count >= PeerFeedbackCapacity) _peerCooldowns.Compact(0.25);
            _peerCooldowns.Set((supplier.SyncPeer.Id, context), (_clock.GetTimestamp(), duration, new WeakReference<PeerInfo>(supplier)),
                new MemoryCacheEntryOptions { Size = 1 });
        }
        _preferredPeers.TryRemove(new KeyValuePair<AllocationContexts, PeerInfo>(context, supplier));
    }

    private bool IsCooling(PeerInfo peer, AllocationContexts context) =>
        _peerCooldowns.TryGetValue((peer.SyncPeer.Id, context), out (long Timestamp, TimeSpan Duration, WeakReference<PeerInfo> Owner) failure)
        && failure.Owner.TryGetTarget(out PeerInfo? owner) && ReferenceEquals(owner, peer)
        && _clock.GetElapsedTime(failure.Timestamp) < failure.Duration;

    internal static Account VerifyAccountRange(Hash256 stateRoot, Address address, AccountsAndProofs response, ILogManager logManager) =>
        ExportAccountProof(stateRoot, address, response, logManager).Account;

    internal static UInt256 VerifyStorageRange(Account account, ValueHash256 accountPath, UInt256 key, SlotsAndProofs response, ILogManager logManager)
        => ExportStorageProof(account, accountPath, key, response, logManager).Value;

    private static void ValidateAccountRange(AccountsAndProofs response)
    {
        ValidateProof(response.Proofs);
        if (response.PathAndAccounts.Count > 4)
            throw new InvalidDataException("Execution peer returned too many accounts for a single-key range.");
    }

    private static void ValidateStorageRange(SlotsAndProofs response)
    {
        ValidateProof(response.Proofs);
        if (response.PathsAndSlots.Count > 1 || response.PathsAndSlots.Count == 1 && response.PathsAndSlots[0].Count > 4)
            throw new InvalidDataException("Execution peer returned an invalid storage range size.");
    }

    private static Account FindAccount(ValueHash256 path, IReadOnlyList<PathWithAccount> accounts)
    {
        foreach (PathWithAccount entry in accounts)
            if (entry.Path == path) return entry.Account.IsNull
                ? throw new InvalidDataException("Account range omitted its account value.")
                : entry.Account.ToAccount();
        return Account.TotallyEmpty;
    }

    private static UInt256 FindStorage(ValueHash256 slotPath, IReadOnlyList<PathWithStorageSlot> slots)
    {
        foreach (PathWithStorageSlot entry in slots)
            if (entry.Path == slotPath) return ExecutionProofVerifier.DecodeStorageRlp(entry.SlotRlpValue);
        return UInt256.Zero;
    }

    private static void ValidateProof(IByteArrayList proof)
    {
        if (proof.Count > ExecutionProofVerifier.MaxProofNodes) throw new InvalidDataException("SNAP proof has too many nodes.");
        for (int i = 0; i < proof.Count; i++)
            if (proof[i].Length is 0 or > ExecutionProofVerifier.MaxNodeBytes)
                throw new InvalidDataException("SNAP proof contains an invalid node size.");
    }

    public async ValueTask DisposeAsync()
    {
        FinalizedHeaderHistory? history;
        lock (_historyLifetime)
        {
            _historyStopped = true;
            history = _logHistory.IsValueCreated ? _logHistory.Value : null;
        }
        // The history must stop using the transport before it is torn down.
        Task? historyDisposal = history?.DisposeAsync().AsTask();
        if (historyDisposal is not null) await historyDisposal.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await _stop.CancelAsync();
        await _bodies.DisposeAsync();
        await _receipts.DisposeAsync();
        await _headers.DisposeAsync();
        _consensusHeaderRequests.Dispose();
        await _accounts.DisposeAsync();
        await _storage.DisposeAsync();
        await _code.DisposeAsync();
        await _blockAccessLists.DisposeAsync();
        // Callers must leave the selector before its cooldown cache and services are disposed.
        for (int i = 0; i < ExecutionRequestLimit; i++) await _executionDataRequests.WaitAsync();
        for (int i = 0; i < ExecutionBulkLimit; i++) await _executionBulkRequests.WaitAsync();
        for (int i = 0; i < SnapRequestLimit; i++) await _snapRequests.WaitAsync();
        if (_snapCapacityTask is not null)
        {
            try { await _snapCapacityTask; }
            catch (OperationCanceledException) { }
            catch (Exception exception) { _logger.Error("Execution SNAP capacity maintenance failed during shutdown", exception); }
        }
        if (_feederTask is not null)
        {
            try { await _feederTask; }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                if (_logger.IsWarn) _logger.Warn($"Execution peer discovery stopped: {exception.Message}");
            }
        }
        _sessionMonitor?.Stop();
        if (_peerManager is not null) await _peerManager.StopAsync();
        if (_peerPool is not null) await _peerPool.StopAsync();
        if (_discovery is not null) await _discovery.StopAsync();
        if (_host is not null) await _host.Shutdown();
        await _services.DisposeAsync();
        _peerCooldowns.Dispose();
        _stop.Dispose();
        if (historyDisposal is not null) await historyDisposal;
    }

    private sealed class LightExecutionOverrides(LightSyncServer syncServer, BoundedTransactionRelay transactionRelay,
        CancellationToken stopToken) : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder.RegisterInstance(new ProcessExitSource(stopToken)).As<IProcessExitSource>();
            builder.RegisterInstance(new EthereumJsonSerializer()).As<IJsonSerializer>();
            // This processing scope builds genesis; proof-backed calls use their own caches.
            builder.RegisterInstance(new PreBlockCachesConfig { StateCacheSetsBits = 1, StorageCacheSetsBits = 1 });
            builder.RegisterInstance(transactionRelay).As<ITxPool>();
            builder.RegisterInstance(LightTxGossipPolicy.Instance).As<ITxGossipPolicySource>();
            builder.RegisterInstance(new LightSnapServer()).As<ISnapServer>();
            builder.RegisterInstance(syncServer).As<ISyncServer>();
            builder.RegisterInstance(new LightGossipPolicy()).As<IGossipPolicy>();
            builder.RegisterType<LightPeerRequestScheduler>().As<IBackgroundTaskScheduler>().SingleInstance();
            builder.RegisterType<ProtocolsManager>()
                .As<IProtocolsManager>()
                .As<IProtocolRegistrar>()
                .WithParameter(new TypedParameter(typeof(IP2PCapabilityResolver[]), new IP2PCapabilityResolver[] { new LightCapabilities() }))
                .WithAttributeFiltering()
                .SingleInstance();
        }
    }

    /// <summary>Returns unavailable SNAP data while retaining sessions used for downloads.</summary>
    private sealed class LightSnapServer : ISnapServer
    {
        public bool CanServe => true;

        public IByteArrayList GetTrieNodes(IReadOnlyList<PathGroup> pathSet, Hash256 rootHash, long byteLimit,
            CancellationToken cancellationToken) => EmptyByteArrayList.Instance;

        public IByteArrayList GetByteCodes(IReadOnlyList<ValueHash256> requestedHashes, long byteLimit,
            CancellationToken cancellationToken) => EmptyByteArrayList.Instance;

        public IByteArrayList GetBlockAccessLists(IReadOnlyList<ValueHash256> blockHashes, long byteLimit,
            CancellationToken cancellationToken)
        {
            int count = (int)Math.Min(blockHashes.Count, Math.Clamp(byteLimit, 0L, ISnapStateServer.HardResponseByteLimit));
            if (count == 0 || cancellationToken.IsCancellationRequested) return EmptyByteArrayList.Instance;

            ArrayPoolList<byte[]> entries = [with(count)];
            try
            {
                for (int i = 0; i < count && !cancellationToken.IsCancellationRequested; i++) entries.Add([]);
                return new ByteArrayListAdapter(entries);
            }
            catch
            {
                entries.Dispose();
                throw;
            }
        }

        public (IOwnedReadOnlyList<PathWithAccount>, IByteArrayList) GetAccountRanges(Hash256 rootHash,
            in ValueHash256 startingHash, in ValueHash256? limitHash, long byteLimit,
            CancellationToken cancellationToken) => (ArrayPoolList<PathWithAccount>.Empty(), EmptyByteArrayList.Instance);

        public (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>>, IByteArrayList) GetStorageRanges(
            Hash256 rootHash, IReadOnlyList<PathWithAccount> accounts, in ValueHash256? startingHash,
            in ValueHash256? limitHash, long byteLimit, CancellationToken cancellationToken) =>
            (ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>.Empty(), EmptyByteArrayList.Instance);
    }

    private sealed class LightCapabilities : IP2PCapabilityResolver
    {
        public event Action? Changed { add { } remove { } }
        public void Resolve(ISet<Capability> capabilities)
        {
            for (byte version = 68; version <= 72; version++)
                capabilities.Add(new Capability(Protocol.Eth, version));
            capabilities.Add(new Capability(Protocol.Snap, 1));
            capabilities.Add(new Capability(Protocol.Snap, 2));
        }
    }

    private sealed class LightGossipPolicy : IGossipPolicy
    {
        public bool CanGossipBlocks => false;
        public bool ShouldDiscardBlocks => true;
        public bool ShouldDisconnectGossipingNodes => false;
    }

    private sealed class LightTxGossipPolicy : ITxGossipPolicy, ITxGossipPolicySource
    {
        internal static readonly LightTxGossipPolicy Instance = new();
        public ITxGossipPolicy[] Policies { get; }
        private LightTxGossipPolicy() => Policies = [this];
        public bool ShouldListenToGossipedTransactions => false;
        public bool CanGossipTransactions => false;
        public bool ShouldGossipTransaction(Transaction tx) => false;
    }

    internal sealed class LightSyncServer : ISyncServer
    {
        private sealed record AdvertisedHeader(BlockHeader Header, byte[]? Rlp);
        private sealed record AdvertisedHeads(AdvertisedHeader Current, AdvertisedHeader? Previous);

        private readonly ChainSpec _chainSpec;
        private readonly Lock _receiptHeadersLock = new();
        private readonly Dictionary<Hash256, BlockHeader> _receiptHeaders = [with(ExecutionBulkLimit)];
        private AdvertisedHeads _heads;

        internal LightSyncServer(ChainSpec chainSpec, VerifiedHead? head, ulong timestamp)
        {
            _chainSpec = chainSpec;
            _heads = new(new(chainSpec.Genesis!.Header, null), null);
            if (head is not null) UpdateHead(head, timestamp);
        }

        internal void UpdateHead(VerifiedHead head, ulong timestamp, byte[]? encodedHeader = null)
        {
            if (head.Number == 0 && head.BlockHash != _chainSpec.Genesis!.Hash) return;
            AdvertisedHeads before = Volatile.Read(ref _heads);
            bool sameHash = before.Current.Header.Hash == head.BlockHash;
            // Re-encoding a wire header can lose optional field presence. Only serve bytes that still match the authenticated hash.
            byte[]? rlp = encodedHeader is not null && Keccak.Compute(encodedHeader) == head.BlockHash ? encodedHeader
                : sameHash ? before.Current.Rlp : null;
            Volatile.Write(ref _heads, new(new(CreateHead(head, timestamp), rlp), sameHash ? before.Previous : before.Current));
        }

        internal void AddReceiptHeader(BlockHeader header)
        {
            lock (_receiptHeadersLock) _receiptHeaders.Add(header.Hash!, header);
        }

        internal void RemoveReceiptHeader(BlockHeader header)
        {
            lock (_receiptHeadersLock)
                if (_receiptHeaders.TryGetValue(header.Hash!, out BlockHeader? current) && ReferenceEquals(current, header))
                    _receiptHeaders.Remove(header.Hash!);
        }

        private BlockHeader CreateHead(VerifiedHead head, ulong timestamp) => new(
            Keccak.Zero, Keccak.OfAnEmptySequenceRlp, Address.Zero, UInt256.Zero,
            head.Number, 0, timestamp, [])
        {
            Hash = CopyHash(head.BlockHash),
            StateRoot = CopyHash(head.StateRoot),
            TotalDifficulty = _chainSpec.Parameters.TerminalTotalDifficulty ?? UInt256.Zero,
        };

        public ulong NetworkId => _chainSpec.NetworkId;
        public BlockHeader Genesis => _chainSpec.Genesis!.Header;
        public BlockHeader Head => Volatile.Read(ref _heads).Current.Header;
        public ulong LowestBlock => Head.Number;
        public void HintBlock(Hash256 hash, ulong number, ISyncPeer receivedFrom) { }
        public void AddNewBlock(Block block, ISyncPeer node) { }
        public TxReceipt[]? GetReceipts(Hash256 blockHashes) => null;
        public bool TryAppendReceipts(Hash256 blockHash, IRlpDecoder<TxReceipt> decoder, bool withBloom, ArrayPoolList<byte> destination) => false;
        public System.Buffers.MemoryManager<byte>? GetBlockAccessListRlp(Hash256 blockHash) => null;
        public System.Buffers.MemoryManager<byte>? FindBlockRlp(Hash256 hash)
        {
            if (hash != Genesis.Hash) return null;
            byte[] encoded = Rlp.Encode(_chainSpec.Genesis!);
            return Keccak.Compute(ReadBlockHeader(encoded)) == hash ? new ArrayMemoryManager(encoded) : null;
        }
        public BlockHeader? FindHeader(Hash256 hash)
        {
            if (hash == Genesis.Hash) return Genesis;
            lock (_receiptHeadersLock) return _receiptHeaders.GetValueOrDefault(hash);
        }
        public Hash256? FindHash(ulong number)
        {
            if (number == 0) return Genesis.Hash;
            AdvertisedHeads heads = Volatile.Read(ref _heads);
            if (heads.Current is { Rlp: not null } current && current.Header.Number == number) return current.Header.Hash;
            return heads.Previous is { Rlp: not null } previous && previous.Header.Number == number ? previous.Header.Hash : null;
        }

        public IOwnedReadOnlyList<byte> FindHeadersRlp(Hash256 hash, int numberOfBlocks, int skip, bool reverse)
        {
            if (numberOfBlocks <= 0) return IOwnedReadOnlyList<byte>.Empty;
            if (hash == Genesis.Hash)
            {
                ReadOnlySpan<byte> genesis = ReadBlockHeader(Rlp.Encode(_chainSpec.Genesis!));
                return Keccak.Compute(genesis) == hash ? new ArrayPoolList<byte>(genesis) : IOwnedReadOnlyList<byte>.Empty;
            }
            AdvertisedHeads heads = Volatile.Read(ref _heads);
            byte[]? encoded = heads.Current.Header.Hash == hash ? heads.Current.Rlp
                : heads.Previous is { } previous && previous.Header.Hash == hash ? previous.Rlp : null;
            return encoded is not null ? new ArrayPoolList<byte>(encoded.AsSpan()) : IOwnedReadOnlyList<byte>.Empty;
        }

        private static ReadOnlySpan<byte> ReadBlockHeader(ReadOnlySpan<byte> encodedBlock)
        {
            RlpReader block = new(encodedBlock);
            RlpReader fields = block.EnterSequence(out _);
            return fields.ReadEncodedItem();
        }
        public IByteArrayList GetNodeData(IReadOnlyList<Hash256> keys, CancellationToken cancellationToken,
            NodeDataType includedTypes = NodeDataType.Code | NodeDataType.State) => EmptyByteArrayList.Instance;
        public int GetPeerCount() => 0;
        public void Dispose() { }
    }
}
