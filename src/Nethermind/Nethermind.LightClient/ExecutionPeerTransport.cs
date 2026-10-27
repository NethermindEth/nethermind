// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Autofac;
using Autofac.Features.AttributeFilters;
using Microsoft.Extensions.Caching.Memory;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Ethash;
using Nethermind.Consensus.Scheduler;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
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
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.Rlpx;
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
using Nethermind.Synchronization;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;

namespace Nethermind.LightClient;

/// <summary>Connects to execution peers without starting block or state synchronization.</summary>
internal sealed class ExecutionPeerTransport : IExecutionStateSource, IAsyncDisposable
{
    private const int ExecutionRequestLimit = 4;
    private const int ExecutionBulkLimit = ExecutionRequestLimit - 1;
    private const int SnapRequestLimit = 8;
    private static readonly HeaderDecoder _logHeaderDecoder = new();
    private readonly IContainer _services;
    private readonly ILogManager _logManager;
    private readonly Nethermind.Logging.ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly VerifiedPayloadCache<Block> _bodies;
    private readonly VerifiedPayloadCache<TxReceipt[]> _receipts;
    private readonly VerifiedPayloadCache<BlockHeader> _headers;
    private readonly VerifiedPayloadCache<Account> _accounts;
    private readonly VerifiedPayloadCache<UInt256> _storage;
    private readonly VerifiedPayloadCache<byte[]> _code;
    private readonly SemaphoreSlim _executionDataRequests = new(ExecutionRequestLimit, ExecutionRequestLimit);
    private readonly SemaphoreSlim _executionBulkRequests = new(ExecutionBulkLimit, ExecutionBulkLimit);
    private readonly MemoryCache _peerCooldowns = new(new MemoryCacheOptions { SizeLimit = 256 });
    private readonly ConcurrentDictionary<AllocationContexts, PeerInfo> _preferredPeers = new();
    private readonly ConcurrentDictionary<AllocationContexts, PeerInfo> _lastPeers = new();
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _snapRequests = new(SnapRequestLimit, SnapRequestLimit);
    private readonly LightSyncServer _syncServer;
    private readonly BoundedTransactionRelay _transactionRelay = new();
    private readonly HostedAccountState? _hostedAccounts;
    private readonly Lazy<VerifiedHeaderHistory> _logHistory;
    private readonly Lock _historyLifetime = new();
    private bool _historyStopped;
    private Tuple<Hash256, ReadOnlyBlockAccessList>? _lastBlockAccessList;
    private IRlpxHost? _host;
    private IDiscoveryApp? _discovery;
    private IPeerPool? _peerPool;
    private IPeerManager? _peerManager;
    private ISessionMonitor? _sessionMonitor;
    private Task? _feederTask;
    private Task? _snapCapacityTask;

    internal int PeerCount => _services.Resolve<ISyncPeerPool>().PeerCount;
    internal Nethermind.Core.Specs.ISpecProvider SpecProvider => _services.Resolve<Nethermind.Core.Specs.ISpecProvider>();
    internal int SnapPeerCount => _services.Resolve<ISyncPeerPool>().AllPeers.Count(
        static peer => peer.SyncPeer.TryGetSatelliteProtocol<ISnapSyncPeer>(Protocol.Snap, out _));
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
        _logManager = logManager;
        _logger = logManager.GetClassLogger<ExecutionPeerTransport>();
        if (hostedAccounts is { Length: > 0 })
            _hostedAccounts = new(hostedAccounts, this, DownloadStorageAsync, _logger);
        _ = typeof(EthashChainSpecEngineParameters).Assembly;
        TypeDiscovery.Initialize();
        (string chainSpecFile, Hash256 genesisHash) = network switch
        {
            "mainnet" => ("foundation.json", KnownHashes.MainnetGenesis),
            "hoodi" => ("hoodi.json", KnownHashes.HoodiGenesis),
            "sepolia" => ("sepolia.json", KnownHashes.SepoliaGenesis),
            _ => throw new ArgumentException("Supported networks: mainnet, hoodi, sepolia.", nameof(network)),
        };
        ChainSpec chainSpec = new ChainSpecFileLoader(new EthereumJsonSerializer(), logManager)
            .LoadEmbeddedOrFromFile(chainSpecFile);
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
            DiscoveryDns = $"enrtree://AKA3AM6LPBYEUDMVNU3BSVQJ5AD45Y7YPOHJLEF6W26QOE4VTUDPE@snap.{network}.ethdisco.net",
            P2PPort = port,
            DiscoveryPort = port,
            MaxActivePeers = 32,
            MaxCandidatePeerCount = 4096,
            CandidatePeerCountCleanupThreshold = 4500,
            IsPeersPersistenceOn = false,
            ClientIdMatcher = "(?i)^(?!.*enrscout)",
        };
        SyncConfig sync = new() { SnapSync = true, FastSync = false, SnapServingEnabled = false };
        DiscoveryConfig discovery = new() { DiscoveryVersion = DiscoveryVersion.V5 };
        ConfigProvider config = new(init, net, sync, discovery, new FlatDbConfig { Enabled = false });
        ContainerBuilder builder = new();
        builder.RegisterModule(new NethermindModule(chainSpec, config, logManager));
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
            IDb db = directory is null ? new MemDb("lightClientLogHeaders") : VerifiedHeaderHistory.OpenDb(directory, logManager);
            try
            {
                return new(db, genesisHash, SpecProvider, GetHeaderAsync, FetchLogHeaderPageAsync, _logger,
                    directory: directory);
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
                    if (!IsCooling(peer, AllocationContexts.Snap)
                        && peer.SyncPeer.TryGetSatelliteProtocol<ISnapSyncPeer>(Protocol.Snap, out _)) availableSnap++;
                if (availableSnap >= 8 || pool.InitializedPeersCount < 30) continue;
                foreach (PeerInfo peer in pool.InitializedPeers)
                {
                    if (peer.SyncPeer.IsPriority || !IsCooling(peer, AllocationContexts.Snap)
                        && peer.SyncPeer.TryGetSatelliteProtocol<ISnapSyncPeer>(Protocol.Snap, out _)) continue;
                    peer.SyncPeer.Disconnect(DisconnectReason.DropWorstPeer, "Reserving light-client connection capacity for SNAP peers");
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.Error("Execution SNAP capacity maintenance stopped", exception);
        }
    }

    internal void UpdateHead(VerifiedHead head, ulong timestamp) => _syncServer.UpdateHead(head, timestamp);

    internal Task HostAccountsAsync(Func<VerifiedHead?> getHead, TimeProvider clock, CancellationToken cancellationToken) =>
        _hostedAccounts?.RunAsync(getHead, clock, cancellationToken) ?? Task.Delay(Timeout.Infinite, cancellationToken);

    internal void NotifyHostedHeadChanged() => _hostedAccounts?.NotifyHeadChanged();

    public Task<Account> GetAccountAsync(VerifiedHead head, Address address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_hostedAccounts?.TryGetAccount(head, address, out Account retained) == true)
            return Task.FromResult(retained);
        ValueHash256 path = ValueKeccak.Compute(address.Bytes);
        return _accounts.GetAsync(StateKey(head.StateRoot, address.Bytes), fetchToken => RequestAsync(async (peer, token) =>
        {
            using AccountsAndProofs response = await peer.GetAccountRange(new AccountRange(head.StateRoot, path, path.IncrementPath()), token);
            return VerifyAccountRange(head.StateRoot, address, response, _logManager);
        }, fetchToken), cancellationToken);
    }

    public Task<UInt256> GetStorageAsync(VerifiedHead head, Address address, Account account, UInt256 key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
                Accounts = new ArrayPoolList<PathWithAccount>(1) { new(accountPath, account) },
                StartingHash = slotPath,
                LimitHash = slotPath.IncrementPath(),
            };
            using SlotsAndProofs response = await peer.GetStorageRange(request, token);
            return VerifyStorageRange(account, accountPath, key, response, _logManager);
        }, fetchToken), cancellationToken);
    }

    public async Task<byte[]> GetCodeAsync(Account account, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_hostedAccounts?.TryGetCode(account, out byte[] retained) == true)
            return retained;
        if (!account.HasCode) return [];
        byte[] cached = await _code.GetAsync(account.CodeHash, fetchToken => RequestAsync(async (peer, token) =>
        {
            using IByteArrayList response = await peer.GetByteCodes([new ValueHash256(account.CodeHash.Bytes)], token);
            if (response.Count != 1 || response[0].Length > 1024 * 1024)
                throw new InvalidDataException("Execution peer returned an invalid bytecode response.");
            byte[] code = response[0].ToArray();
            ExecutionProofVerifier.VerifyCode(account.CodeHash, code);
            return code;
        }, fetchToken), cancellationToken);
        return cached.AsSpan().ToArray();
    }

    private static Hash256 StateKey(Hash256 root, ReadOnlySpan<byte> path)
    {
        Span<byte> key = stackalloc byte[32 + path.Length];
        root.Bytes.CopyTo(key);
        path.CopyTo(key[32..]);
        return Keccak.Compute(key);
    }

    private Task<IReadOnlyDictionary<ValueHash256, UInt256>> DownloadStorageAsync(
        VerifiedHead head, Address address, Account account, CancellationToken cancellationToken)
    {
        PathWithAccount accountPath = new(ValueKeccak.Compute(address.Bytes), account);
        return HostedStorageDownload.DownloadAsync(account.StorageRoot, (start, token) => RequestAsync(async (peer, peerToken) =>
        {
            using StorageRange request = new()
            {
                RootHash = head.StateRoot,
                Accounts = new ArrayPoolList<PathWithAccount>(1) { accountPath },
                StartingHash = start,
                LimitHash = ValueKeccak.MaxValue,
            };
            using SlotsAndProofs response = await peer.GetStorageRange(request, peerToken);
            return HostedStorageDownload.VerifyPage(accountPath, start, response, _logManager);
        }, token), cancellationToken);
    }

    public async Task<ExecutionAccountProof> GetProofAsync(VerifiedHead head, Address address, UInt256[] storageKeys,
        CancellationToken cancellationToken)
    {
        (Account account, byte[][] proof) = await RequestAsync(async (peer, token) =>
        {
            ValueHash256 path = ValueKeccak.Compute(address.Bytes);
            using AccountsAndProofs response = await peer.GetAccountRange(new AccountRange(head.StateRoot, path, path.IncrementPath()), token);
            return ExportAccountProof(head, address, response, _logManager);
        }, cancellationToken);
        ExecutionStorageProof[] storage = new ExecutionStorageProof[storageKeys.Length];
        for (int i = 0; i < storage.Length; i++)
        {
            UInt256 key = storageKeys[i];
            if (!account.HasStorage) { storage[i] = new(key, UInt256.Zero, []); continue; }
            storage[i] = await RequestAsync(async (peer, token) =>
            {
                ValueHash256 accountPath = ValueKeccak.Compute(address.Bytes);
                ValueHash256 slotPath = ValueKeccak.Compute(key.ToBigEndian());
                using StorageRange request = new()
                {
                    RootHash = head.StateRoot,
                    Accounts = new ArrayPoolList<PathWithAccount>(1) { new(accountPath, account) },
                    StartingHash = slotPath,
                    LimitHash = slotPath.IncrementPath(),
                };
                using SlotsAndProofs response = await peer.GetStorageRange(request, token);
                return ExportStorageProof(account, accountPath, key, response, _logManager);
            }, cancellationToken);
        }
        return new(account, proof, storage);
    }

    internal static (Account Account, byte[][] Proof) ExportAccountProof(VerifiedHead head, Address address,
        AccountsAndProofs response, ILogManager logs)
    {
        ValueHash256 path = ValueKeccak.Compute(address.Bytes);
        ValidateAccountRange(response);
        using MemDb db = new();
        NodeStorage nodes = new(db, INodeStorage.KeyScheme.Hash);
        if (response.PathAndAccounts.Count != 0)
        {
            PatriciaSnapTrieFactory factory = new(nodes, NullDb.Instance, logs);
            if (SnapProviderHelper.AddAccountRange(factory, head.Number, head.StateRoot, path, path.IncrementPath(),
                    response.PathAndAccounts, response.Proofs).result != AddRangeResult.OK)
                throw new InvalidDataException("Could not reconstruct the verified account path.");
        }
        byte[][] witness = CollectProof(db, nodes, head.StateRoot, path, response.Proofs);
        Account exported = ExecutionProofVerifier.VerifyAccount(head.StateRoot, address, witness);
        if (response.PathAndAccounts.Count != 0 && exported != FindAccount(path, response.PathAndAccounts))
            throw new InvalidDataException("Reconstructed account witness differs from the verified range.");
        return (exported, witness);
    }

    internal static ExecutionStorageProof ExportStorageProof(Account account, ValueHash256 accountPath, UInt256 key,
        SlotsAndProofs response, ILogManager logs)
    {
        ValueHash256 slotPath = ValueKeccak.Compute(key.ToBigEndian());
        ValidateStorageRange(response);
        using MemDb db = new();
        NodeStorage nodes = new(db, INodeStorage.KeyScheme.Hash);
        bool hasSlots = response.PathsAndSlots.Count != 0 && response.PathsAndSlots[0].Count != 0;
        if (hasSlots)
        {
            PatriciaSnapTrieFactory factory = new(nodes, NullDb.Instance, logs);
            if (SnapProviderHelper.AddStorageRange(factory, new(accountPath, account), response.PathsAndSlots[0],
                    slotPath, slotPath.IncrementPath(), response.Proofs).result != AddRangeResult.OK)
                throw new InvalidDataException("Could not reconstruct the verified storage path.");
        }
        byte[][] witness = CollectProof(db, nodes, account.StorageRoot, slotPath, response.Proofs);
        UInt256 value = ExecutionProofVerifier.VerifyStorage(account.StorageRoot, key, witness);
        if (hasSlots && value != FindStorage(slotPath, response.PathsAndSlots[0]))
            throw new InvalidDataException("Reconstructed storage witness differs from the verified range.");
        return new ExecutionStorageProof(key, value, witness);
    }

    private static byte[][] CollectProof(MemDb db, NodeStorage nodes, Hash256 root, ValueHash256 path, IByteArrayList rangeProof)
    {
        for (int i = 0; i < rangeProof.Count; i++) db[Keccak.Compute(rangeProof[i]).Bytes] = rangeProof[i].ToArray();
        if (root == Keccak.EmptyTreeHash) return [];
        PatriciaTree tree = new(new RawScopedTrieStore(nodes), root, allowCommits: false, Nethermind.Logging.NullLogManager.Instance);
        ProofCollector collector = new(path.Bytes.ToArray());
        tree.Accept(collector, root);
        return collector.BuildResult();
    }

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
            if (sent != 0) return;
            await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new RpcException(-32000, "No execution peer was available to relay the transaction.");
    }

    public async Task<BlockHeader> GetHeaderAsync(VerifiedHead head, CancellationToken cancellationToken)
    {
        BlockHeader header = await GetHeaderByHashAsync(head.BlockHash, cancellationToken);
        if (header.Hash != head.BlockHash || header.Number != head.Number || header.StateRoot != head.StateRoot)
            throw new RpcException(-32000, "Execution header does not match the verified finalized payload.");
        return header;
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
        if (Volatile.Read(ref _lastBlockAccessList) is { } cached && cached.Item1 == hash) return cached.Item2;

        ReadOnlyBlockAccessList list = await RequestExecutionAsync(AllocationContexts.BlockAccessLists, async (peer, token) =>
        {
            if (peer.TryGetSatelliteProtocol<ISnapSyncPeer>(Protocol.Snap, out ISnapSyncPeer? snap) && snap.SnapProtocolVersion >= SnapVersions.Snap2)
            {
                using IByteArrayList response = await snap.GetBlockAccessLists([hash.ValueHash256], token);
                if (response.Count != 1) throw new InvalidDataException("Execution peer omitted the requested BAL.");
                return VerifyBlockAccessList(header, response[0]);
            }
            using IOwnedReadOnlyList<byte[]?> responseEth = await peer.GetBlockAccessLists([hash], token);
            if (responseEth.Count != 1 || responseEth[0] is not { } bytes)
                throw new InvalidDataException("Execution peer omitted the requested BAL.");
            return VerifyBlockAccessList(header, bytes);
        }, cancellationToken, static peer => peer.SupportsBlockAccessLists()
            || peer.TryGetSatelliteProtocol<ISnapSyncPeer>(Protocol.Snap, out ISnapSyncPeer? snap) && snap.SnapProtocolVersion >= SnapVersions.Snap2);
        Volatile.Write(ref _lastBlockAccessList, Tuple.Create(hash, list));
        return list;
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
        BlockHeader retained = await _headers.GetAsync(key, stopping => RequestExecutionAsync(AllocationContexts.Headers, async (peer, token) =>
        {
            BlockHeader header = await peer.GetHeadBlockHeader(new Hash256(key.ValueHash256), token)
                ?? throw new IOException("Execution peer did not supply the requested header.");
            BlockHeader snapshot = CopyHeader(header);
            if (snapshot.Hash != key) throw new InvalidDataException("Execution peer returned a header inconsistent with the requested hash.");
            return snapshot;
        }, stopping, failureMessage: "No execution peer supplied the requested execution header."), cancellationToken);
        return CopyHeader(retained);
    }

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
                if (headers is null || headers.Count != count)
                    throw new IOException("Execution peer did not supply the complete ancestor header page.");
                for (int i = 0; i < count; i++)
                {
                    BlockHeader header = headers[i];
                    int index = offset + i;
                    if (header.Hash is null || header.Number != firstNumber + (ulong)index
                        || (index > 0 && header.ParentHash != hashes[index - 1]))
                        throw new InvalidDataException("Execution peer returned an inconsistent ancestor header chain.");
                    hashes[index] = header.Hash;
                }
                offset += count;
            }
            if (hashes[^1] != head.BlockHash)
                throw new InvalidDataException("Execution peer returned an ancestor chain outside the verified head.");
            return hashes;
        }, cancellationToken, failureMessage: "No execution peer supplied an authenticated header chain.");
    }

    public async Task<BlockHeader> GetCanonicalHeaderAsync(VerifiedHead head, ulong number, CancellationToken cancellationToken)
    {
        if (number == head.Number) return await GetHeaderAsync(head, cancellationToken);
        Hash256[] hashes = await GetAncestorHashesAsync(head, number, cancellationToken);
        BlockHeader header = await GetHeaderByHashAsync(hashes[0], cancellationToken);
        if (header.Number != number)
            throw new RpcException(-32000, "Execution peer returned a header outside the verified chain.");
        return header;
    }

    public Task<BlockHeader[]> GetLogHeadersAsync(VerifiedHead anchor, VerifiedHead finalized, ulong firstNumber,
        ulong lastNumber, CancellationToken cancellationToken)
    {
        lock (_historyLifetime)
        {
            ObjectDisposedException.ThrowIf(_historyStopped, this);
            cancellationToken.ThrowIfCancellationRequested();
        }
        return VerifiedLogHeaders.GetAsync(this, anchor, finalized, firstNumber, lastNumber, cancellationToken,
            ReadFinalizedAsync, ReadTransientLogHeadersAsync);

        Task<BlockHeader[]> ReadFinalizedAsync(VerifiedHead head, ulong first, ulong last, CancellationToken token)
        {
            VerifiedHeaderHistory history;
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
        RequestExecutionAsync(AllocationContexts.Headers, (peer, token) => ReadLogHeaderPageAsync(peer, cursor, count, token), cancellationToken);

    internal static async Task<BlockHeader[]> ReadLogHeaderPageAsync(ISyncPeer peer, BlockHeader cursor, int count,
        CancellationToken cancellationToken)
    {
        if (count is < 1 or > VerifiedHeaderHistory.PageSize || cursor.Number < (ulong)count)
            throw new InvalidDataException("Execution peer supplied an incomplete log header page.");
        BlockHeader[] result = new BlockHeader[count];
        ulong first = cursor.Number - (ulong)count;
        int received = 0;
        while (received < count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int requested = count - received;
            using IOwnedReadOnlyList<BlockHeader>? headers = await peer.GetBlockHeaders(first + (ulong)received, requested, 0, cancellationToken);
            if (headers is null || headers.Count == 0 || headers.Count > requested)
                throw new InvalidDataException("Execution peer supplied an incomplete log header page.");
            for (int i = 0; i < headers.Count; i++)
            {
                BlockHeader header = headers[i];
                if (header is null || header.Number != first + (ulong)(received + i)
                    || _logHeaderDecoder.GetLength(header, RlpBehaviors.None) > VerifiedHeaderHistory.MaxHeaderBytes)
                    throw new InvalidDataException("Execution peer supplied an invalid log header page.");
                result[received + i] = header;
            }
            received += headers.Count;
        }
        VerifiedHeaderHistory.VerifyPage(cursor, count, result);
        return result;
    }

    private async Task<BlockHeader[]> ReadTransientLogHeadersAsync(VerifiedHead anchor, ulong first, ulong last,
        CancellationToken cancellationToken)
    {
        BlockHeader cursor = await GetHeaderAsync(anchor, cancellationToken);
        VerifiedLogHeaders.Verify(cursor, anchor.Number, anchor.BlockHash);
        if (cursor.StateRoot != anchor.StateRoot)
            throw new InvalidDataException("Optimistic log anchor has an inconsistent state root.");
        BlockHeader[] result = new BlockHeader[checked((int)(last - first + 1))];
        if (cursor.Number <= last) result[checked((int)(cursor.Number - first))] = cursor;
        while (cursor.Number > first)
        {
            int count = (int)Math.Min(VerifiedHeaderHistory.PageSize, cursor.Number - first);
            BlockHeader[] page = await FetchLogHeaderPageAsync(cursor, count, cancellationToken);
            foreach (BlockHeader header in page)
                if (header.Number <= last) result[checked((int)(header.Number - first))] = header;
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
            if (bodies.Count != 1 || bodies[0] is not { } body)
                throw new InvalidDataException("Execution peer did not supply a complete block body.");
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

    public async Task<TxReceipt[]> GetReceiptsAsync(BlockHeader header, CancellationToken cancellationToken, int? transactionCount = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (transactionCount == 0 || header.ReceiptsRoot == Keccak.EmptyTreeHash)
        {
            VerifyReceipts(header, [], SpecProvider);
            if (transactionCount is > 0) throw new InvalidDataException("Verified receipt count does not match the block transaction count.");
            return Array.Empty<TxReceipt>();
        }
        if ((transactionCount ?? GetReceiptCountLimit(header, SpecProvider.GetSpec(header))) is not { } maximum)
        {
            header = header.Clone();
            maximum = (await GetBlockAsync(header, cancellationToken)).Transactions.Length;
            transactionCount = maximum;
        }
        TxReceipt[] cached = await _receipts.GetAsync(header.Hash!, async stopping =>
        {
            BlockHeader requestHeader = header.Clone();
            int registered = 0;
            try
            {
                return await RequestExecutionAsync(AllocationContexts.Receipts, async (peer, token) =>
                {
                    if (Interlocked.Exchange(ref registered, 1) == 0) _syncServer.AddReceiptHeader(requestHeader);
                    ReadOnlyMemory<int> counts = transactionCount is { } count ? new[] { count } : ReadOnlyMemory<int>.Empty;
                    ReadOnlyMemory<int>? limits = transactionCount is null ? new ReadOnlyMemory<int>(new[] { maximum }) : null;
                    using IOwnedReadOnlyList<TxReceipt[]?> batches = await peer.GetReceipts([requestHeader.Hash!], counts, token, limits);
                    if (batches.Count != 1 || batches[0] is not { } receipts || receipts.Length > maximum ||
                        transactionCount is { } exact && receipts.Length != exact)
                        throw new InvalidDataException("Execution peer did not supply the complete receipt list.");
                    VerifyReceipts(requestHeader, receipts, SpecProvider);
                    TxReceipt[] result = new TxReceipt[receipts.Length];
                    for (int i = 0; i < result.Length; i++) result[i] = new TxReceipt(receipts[i]);
                    return result;
                }, stopping);
            }
            finally
            {
                if (registered != 0) _syncServer.RemoveReceiptHeader(requestHeader);
            }
        }, cancellationToken);
        if (cached.Length > maximum || transactionCount is { } expected && cached.Length != expected)
            throw new InvalidDataException("Verified receipt count does not match the block transaction count.");
        TxReceipt[] copy = new TxReceipt[cached.Length];
        for (int i = 0; i < copy.Length; i++) copy[i] = new TxReceipt(cached[i]);
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
        if (ReceiptTrie.CalculateRoot(specProvider.GetSpec(header), receipts, new ReceiptMessageDecoder()) != header.ReceiptsRoot)
            throw new InvalidDataException("Execution peer returned receipts inconsistent with the verified header.");
    }

    private Task<T> RequestExecutionAsync<T>(AllocationContexts context, Func<ISyncPeer, CancellationToken, Task<T>> request,
        CancellationToken cancellationToken, Func<ISyncPeer, bool>? canServe = null,
        string failureMessage = "No execution peer supplied authenticated block data.") =>
        RequestFromPeersAsync(context, static peer => peer, request, cancellationToken, canServe, failureMessage);

    private Task<T> RequestAsync<T>(Func<ISnapSyncPeer, CancellationToken, Task<T>> request, CancellationToken cancellationToken) =>
        RequestFromPeersAsync(AllocationContexts.Snap,
            static peer => peer.TryGetSatelliteProtocol<ISnapSyncPeer>(Protocol.Snap, out ISnapSyncPeer? snap) ? snap : null,
            request, cancellationToken, null, "No execution peer supplied a valid SNAP proof for the selected verified state.");

    private async Task<T> RequestFromPeersAsync<TPeer, T>(AllocationContexts context, Func<ISyncPeer, TPeer?> selectPeer,
        Func<TPeer, CancellationToken, Task<T>> request, CancellationToken cancellationToken,
        Func<ISyncPeer, bool>? canServe, string failureMessage) where TPeer : class
    {
        SemaphoreSlim requests = context == AllocationContexts.Snap ? _snapRequests : _executionDataRequests;
        bool bulk = context is not (AllocationContexts.Headers or AllocationContexts.Snap);
        bool bulkAcquired = false;
        bool requestAcquired = false;
        CancellationToken stopToken = _stop.Token;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30), _clock);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopToken, timeout.Token);
        try
        {
            if (bulk)
            {
                await _executionBulkRequests.WaitAsync(deadline.Token);
                bulkAcquired = true;
            }
            await requests.WaitAsync(deadline.Token);
            requestAcquired = true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !stopToken.IsCancellationRequested)
        {
            string kind = context == AllocationContexts.Snap ? "SNAP" : "execution data";
            throw new RpcException(-32000, $"Timed out waiting for an available {kind} request slot.");
        }
        finally
        {
            if (!requestAcquired && bulkAcquired) _executionBulkRequests.Release();
        }
        try
        {
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
                List<(PeerInfo Peer, Task<(T Result, Exception? Error)> Request)> pending = new(2);
                int next = 0;
                try
                {
                    while ((next < eligible.Count || pending.Count != 0) && !deadline.IsCancellationRequested)
                    {
                        do
                        {
                            if (next == eligible.Count || pending.Count == 2) break;
                            (PeerInfo peer, TPeer protocol) = eligible[next++];
                            if (!CanPrefer(peer)) continue;
                            _lastPeers[context] = peer;
                            pending.Add((peer, RequestPeerAsync(protocol, round.Token)));
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
                            while (CanPrefer(supplier))
                            {
                                if (_preferredPeers.TryAdd(context, supplier)) break;
                                if (_preferredPeers.TryGetValue(context, out PeerInfo? current)
                                    && (CanPrefer(current) || _preferredPeers.TryUpdate(context, supplier, current))) break;
                            }
                            return result;
                        }
                        if (error is ConcurrencyLimitReachedException)
                        {
                            if (_logger.IsDebug) _logger.Debug($"Execution {context} peer {supplier.SyncPeer.Node:c} is busy: {error.Message}");
                            continue;
                        }
                        TimeSpan duration = context == AllocationContexts.Snap && error is not InvalidDataException
                            ? TimeSpan.FromSeconds(15) : TimeSpan.FromMinutes(1);
                        _peerCooldowns.Set((supplier.SyncPeer.Id, context), (_clock.GetTimestamp(), duration), new MemoryCacheEntryOptions { Size = 1 });
                        _preferredPeers.TryRemove(new KeyValuePair<AllocationContexts, PeerInfo>(context, supplier));
                        pool.ReportWeakPeer(supplier, context);
                        if (_logger.IsWarn)
                        {
                            string detail = $"{error.GetType().Name}: {error.Message}";
                            _logger.Warn(context == AllocationContexts.Snap
                                ? $"SNAP peer {supplier.SyncPeer.Node:c} could not serve the selected state: {detail}"
                                : $"Execution {context} peer {supplier.SyncPeer.Node:c} failed: {detail}");
                        }
                    }
                }
                finally
                {
                    await round.CancelAsync();
                    foreach ((PeerInfo _, Task<(T Result, Exception? Error)> pendingRequest) in pending) await pendingRequest;
                    cancellationToken.ThrowIfCancellationRequested();
                    stopToken.ThrowIfCancellationRequested();
                    if (timeout.IsCancellationRequested) throw new RpcException(-32000, failureMessage);
                }
                await Task.Delay(TimeSpan.FromSeconds(1), _clock, deadline.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            cancellationToken.ThrowIfCancellationRequested();
            stopToken.ThrowIfCancellationRequested();
            throw new RpcException(-32000, failureMessage);

            async Task<(T Result, Exception? Error)> RequestPeerAsync(TPeer peer, CancellationToken roundToken)
            {
                try
                {
                    using CancellationTokenSource peerTimeout = new(TimeSpan.FromSeconds(10), _clock);
                    using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(roundToken, peerTimeout.Token);
                    return (await request(peer, attempt.Token), null);
                }
                catch (Exception exception) { return (default!, exception); }
            }
        }
        finally
        {
            requests.Release();
            if (bulkAcquired) _executionBulkRequests.Release();
        }
    }

    private bool IsCooling(PeerInfo peer, AllocationContexts context) =>
        _peerCooldowns.TryGetValue((peer.SyncPeer.Id, context), out (long Timestamp, TimeSpan Duration) failure)
        && _clock.GetElapsedTime(failure.Timestamp) < failure.Duration;

    internal static Account VerifyAccountRange(Hash256 stateRoot, Address address, AccountsAndProofs response, ILogManager logManager)
    {
        ValueHash256 path = ValueKeccak.Compute(address.Bytes);
        ValidateAccountRange(response);
        if (response.PathAndAccounts.Count == 0)
            return ExecutionProofVerifier.VerifyAccount(stateRoot, address, CopyProof(response.Proofs));

        using MemDb db = new();
        PatriciaSnapTrieFactory factory = new(new NodeStorage(db), NullDb.Instance, logManager);
        if (SnapProviderHelper.VerifyAccountRange(factory, stateRoot, path, path.IncrementPath(), response.PathAndAccounts, response.Proofs) != AddRangeResult.OK)
            throw new InvalidDataException("Execution peer returned an invalid account range proof.");
        return FindAccount(path, response.PathAndAccounts);
    }

    internal static UInt256 VerifyStorageRange(Account account, ValueHash256 accountPath, UInt256 key, SlotsAndProofs response, ILogManager logManager)
    {
        ValueHash256 slotPath = ValueKeccak.Compute(key.ToBigEndian());
        ValidateStorageRange(response);
        if (response.PathsAndSlots.Count == 0 || response.PathsAndSlots.Count == 1 && response.PathsAndSlots[0].Count == 0)
            return ExecutionProofVerifier.VerifyStorage(account.StorageRoot, key, CopyProof(response.Proofs));

        using MemDb db = new();
        PatriciaSnapTrieFactory factory = new(new NodeStorage(db), NullDb.Instance, logManager);
        if (SnapProviderHelper.AddStorageRange(factory, new PathWithAccount(accountPath, account), response.PathsAndSlots[0],
                slotPath, slotPath.IncrementPath(), response.Proofs).result != AddRangeResult.OK)
            throw new InvalidDataException("Execution peer returned an invalid storage range proof.");
        return FindStorage(slotPath, response.PathsAndSlots[0]);
    }

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
            if (entry.Path == path) return entry.Account ?? throw new InvalidDataException("Account range omitted its account value.");
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

    private static byte[][] CopyProof(IByteArrayList proof)
    {
        byte[][] result = new byte[proof.Count][];
        for (int i = 0; i < result.Length; i++) result[i] = proof[i].ToArray();
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        VerifiedHeaderHistory? history;
        lock (_historyLifetime)
        {
            _historyStopped = true;
            history = _logHistory.IsValueCreated ? _logHistory.Value : null;
        }
        await _stop.CancelAsync();
        await _bodies.DisposeAsync();
        await _receipts.DisposeAsync();
        await _headers.DisposeAsync();
        await _accounts.DisposeAsync();
        await _storage.DisposeAsync();
        await _code.DisposeAsync();
        // Callers must leave the selector before its cooldown cache and services are disposed.
        for (int i = 0; i < ExecutionRequestLimit; i++) await _executionDataRequests.WaitAsync();
        for (int i = 0; i < ExecutionBulkLimit; i++) await _executionBulkRequests.WaitAsync();
        for (int i = 0; i < SnapRequestLimit; i++) await _snapRequests.WaitAsync();
        if (history is not null) await history.DisposeAsync();
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
    }

    private sealed class LightExecutionOverrides(LightSyncServer syncServer, BoundedTransactionRelay transactionRelay,
        CancellationToken stopToken) : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder.RegisterInstance(new ProcessExitSource(stopToken)).As<IProcessExitSource>();
            builder.RegisterInstance(new EthereumJsonSerializer()).As<IJsonSerializer>();
            builder.RegisterInstance(new MemDbFactory()).As<IDbFactory>();
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

            ArrayPoolList<byte[]> entries = new(count);
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
        private readonly ChainSpec _chainSpec;
        private readonly Lock _receiptHeadersLock = new();
        private readonly Dictionary<Hash256, BlockHeader> _receiptHeaders = new(ExecutionBulkLimit);
        private BlockHeader _head;

        internal LightSyncServer(ChainSpec chainSpec, VerifiedHead? head, ulong timestamp)
        {
            _chainSpec = chainSpec;
            _head = chainSpec.Genesis!.Header;
            if (head is not null) UpdateHead(head, timestamp);
        }

        internal void UpdateHead(VerifiedHead head, ulong timestamp)
        {
            if (head.Number == 0 && head.BlockHash != _chainSpec.Genesis!.Hash) return;
            Volatile.Write(ref _head, CreateHead(head, timestamp));
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
            Hash = head.BlockHash,
            StateRoot = head.StateRoot,
            TotalDifficulty = _chainSpec.Parameters.TerminalTotalDifficulty ?? UInt256.Zero,
        };

        public ulong NetworkId => _chainSpec.NetworkId;
        public BlockHeader Genesis => _chainSpec.Genesis!.Header;
        public BlockHeader? Head => Volatile.Read(ref _head);
        public ulong LowestBlock => 0;
        public void HintBlock(Hash256 hash, ulong number, ISyncPeer receivedFrom) { }
        public void AddNewBlock(Block block, ISyncPeer node) { }
        public void StopNotifyingPeersAboutNewBlocks() { }
        public TxReceipt[]? GetReceipts(Hash256 blockHashes) => null;
        public System.Buffers.MemoryManager<byte>? GetBlockAccessListRlp(Hash256 blockHash) => null;
        public Block? Find(Hash256 hash) => hash == Genesis.Hash ? _chainSpec.Genesis : null;
        public BlockHeader? FindHeader(Hash256 hash)
        {
            if (hash == Genesis.Hash) return Genesis;
            lock (_receiptHeadersLock) return _receiptHeaders.GetValueOrDefault(hash);
        }
        public Hash256? FindHash(ulong number) => number == 0 ? Genesis.Hash : null;
        public IOwnedReadOnlyList<BlockHeader> FindHeaders(Hash256 hash, int numberOfBlocks, int skip, bool reverse) => IOwnedReadOnlyList<BlockHeader>.Empty;
        public IByteArrayList GetNodeData(IReadOnlyList<Hash256> keys, CancellationToken cancellationToken,
            NodeDataType includedTypes = NodeDataType.Code | NodeDataType.State) => EmptyByteArrayList.Instance;
        public int GetPeerCount() => 0;
        public void Dispose() { }
    }
}
