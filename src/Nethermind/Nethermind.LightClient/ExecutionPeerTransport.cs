// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Db;
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
    private readonly IContainer _services;
    private readonly ILogManager _logManager;
    private readonly Nethermind.Logging.ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _executionDataRequests = new(4, 4);
    private readonly SemaphoreSlim _snapRequests = new(8);
    private readonly MemoryCache _snapCooldowns = new(new MemoryCacheOptions { SizeLimit = 256 });
    private PeerInfo? _preferredSnapPeer;
    private readonly LightSyncServer _syncServer;
    private readonly BoundedTransactionRelay _transactionRelay = new();
    private readonly HostedAccountState? _hostedAccounts;
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

    internal ExecutionPeerTransport(string network, ILogManager logManager, VerifiedHead head, ulong timestamp,
        string? dataDirectory = null, Address[]? hostedAccounts = null, bool hostedAccountsBal = false, int port = 30307)
    {
        _logManager = logManager;
        _logger = logManager.GetClassLogger<ExecutionPeerTransport>();
        if (hostedAccounts is { Length: > 0 })
            _hostedAccounts = new(hostedAccounts, this, DownloadStorageAsync, _logger, hostedAccountsBal);
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
        NetworkConfig net = new()
        {
            DiscoveryDns = $"enrtree://AKA3AM6LPBYEUDMVNU3BSVQJ5AD45Y7YPOHJLEF6W26QOE4VTUDPE@snap.{network}.ethdisco.net",
            P2PPort = port,
            DiscoveryPort = port,
            MaxActivePeers = 16,
            MaxCandidatePeerCount = 2048,
            CandidatePeerCountCleanupThreshold = 2200,
            IsPeersPersistenceOn = false,
            ClientIdMatcher = "(?i)^(?!.*enrscout)",
        };
        SyncConfig sync = new() { SnapSync = true, FastSync = false };
        DiscoveryConfig discovery = new() { DiscoveryVersion = DiscoveryVersion.V5 };
        ConfigProvider config = new(init, net, sync, discovery, new FlatDbConfig { Enabled = false });
        ContainerBuilder builder = new();
        builder.RegisterModule(new NethermindModule(chainSpec, config, logManager));
        bool snap2 = chainSpec.Parameters.Eip7928TransitionTimestamp is ulong activation && timestamp >= activation;
        builder.RegisterModule(new LightExecutionOverrides(_syncServer, _transactionRelay, _stop.Token, snap2));
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
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(10));
        ISyncPeerPool pool = _services.Resolve<ISyncPeerPool>();
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                int availableSnap = 0;
                foreach (PeerInfo peer in pool.InitializedPeers)
                    if (!_snapCooldowns.TryGetValue(peer.SyncPeer.Id, out _)
                        && peer.SyncPeer.TryGetSatelliteProtocol<ISnapSyncPeer>(Protocol.Snap, out _)) availableSnap++;
                if (availableSnap >= 4 || pool.InitializedPeersCount < 14) continue;
                foreach (PeerInfo peer in pool.InitializedPeers)
                {
                    if (peer.SyncPeer.IsPriority || !_snapCooldowns.TryGetValue(peer.SyncPeer.Id, out _)
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

    public Task<Account> GetAccountAsync(VerifiedHead head, Address address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_hostedAccounts?.TryGetAccount(head, address, out Account retained) == true)
            return Task.FromResult(retained);
        ValueHash256 path = ValueKeccak.Compute(address.Bytes);
        return RequestAsync(async (peer, token) =>
        {
            using AccountsAndProofs response = await peer.GetAccountRange(new AccountRange(head.StateRoot, path, path.IncrementPath()), token);
            return VerifyAccountRange(head.StateRoot, address, response, _logManager);
        }, cancellationToken);
    }

    public Task<UInt256> GetStorageAsync(VerifiedHead head, Address address, Account account, UInt256 key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_hostedAccounts?.TryGetStorage(address, account, key, out UInt256 retained) == true)
            return Task.FromResult(retained);
        if (!account.HasStorage) return Task.FromResult(UInt256.Zero);
        ValueHash256 accountPath = ValueKeccak.Compute(address.Bytes);
        ValueHash256 slotPath = ValueKeccak.Compute(key.ToBigEndian());
        return RequestAsync(async (peer, token) =>
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
        }, cancellationToken);
    }

    public Task<byte[]> GetCodeAsync(Account account, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_hostedAccounts?.TryGetCode(account, out byte[] retained) == true)
            return Task.FromResult(retained);
        if (!account.HasCode) return Task.FromResult(Array.Empty<byte>());
        return RequestAsync(async (peer, token) =>
        {
            using IByteArrayList response = await peer.GetByteCodes([new ValueHash256(account.CodeHash.Bytes)], token);
            if (response.Count != 1 || response[0].Length > 1024 * 1024)
                throw new InvalidDataException("Execution peer returned an invalid bytecode response.");
            byte[] code = response[0].ToArray();
            ExecutionProofVerifier.VerifyCode(account.CodeHash, code);
            return code;
        }, cancellationToken);
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
        if (header.Number != head.Number || header.StateRoot != head.StateRoot)
            throw new RpcException(-32000, "Execution header does not match the verified finalized payload.");
        return header;
    }

    public async Task<ReadOnlyBlockAccessList> GetBlockAccessListAsync(BlockHeader header, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReleaseSpec spec = SpecProvider.GetSpec(header);
        if (!spec.BlockLevelAccessListsEnabled || !spec.IsEip6780Enabled || header.BlockAccessListHash is null)
            throw new NotSupportedException("BAL state updates are not enabled for this block's fork.");
        Hash256 hash = header.Hash ?? throw new InvalidDataException("BAL lookup requires an authenticated block hash.");
        if (Volatile.Read(ref _lastBlockAccessList) is { } cached && cached.Item1 == hash) return cached.Item2;

        ReadOnlyBlockAccessList list = await RequestExecutionAsync(async (peer, token) =>
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
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        ISyncPeerPool pool = _services.Resolve<ISyncPeerPool>();
        while (!deadline.IsCancellationRequested)
        {
            foreach (PeerInfo peer in pool.AllPeers)
            {
                try
                {
                    using CancellationTokenSource peerTimeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    peerTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                    BlockHeader? header = await peer.SyncPeer.GetHeadBlockHeader(hash, peerTimeout.Token);
                    if (header is not null && header.Hash == hash)
                        return header;
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { break; }
                catch (Exception exception) when (exception is not OperationCanceledException || !deadline.IsCancellationRequested)
                {
                    if (_logger.IsWarn) _logger.Warn($"Execution header peer {peer.SyncPeer.Node:c} failed: {exception.Message}");
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new RpcException(-32000, "No execution peer supplied the requested execution header.");
    }

    public async Task<Hash256[]> GetAncestorHashesAsync(VerifiedHead head, ulong firstNumber, CancellationToken cancellationToken)
    {
        if (firstNumber >= head.Number || head.Number - firstNumber > 256)
            throw new ArgumentOutOfRangeException(nameof(firstNumber));
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        ISyncPeerPool pool = _services.Resolve<ISyncPeerPool>();
        while (!deadline.IsCancellationRequested)
        {
            foreach (PeerInfo peer in pool.AllPeers)
            {
                try
                {
                    using CancellationTokenSource peerTimeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    peerTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                    Hash256[] hashes = new Hash256[checked((int)(head.Number - firstNumber + 1))];
                    bool valid = true;
                    for (int offset = 0; offset < hashes.Length;)
                    {
                        int count = Math.Min(128, hashes.Length - offset);
                        using IOwnedReadOnlyList<BlockHeader>? headers = await peer.SyncPeer.GetBlockHeaders(firstNumber + (ulong)offset,
                            count, 0, peerTimeout.Token);
                        if (headers is null || headers.Count != count) { valid = false; break; }
                        for (int i = 0; i < count; i++)
                        {
                            BlockHeader header = headers[i];
                            int index = offset + i;
                            if (header.Hash is null || header.Number != firstNumber + (ulong)index
                                || (index > 0 && header.ParentHash != hashes[index - 1])) valid = false;
                            hashes[index] = header.Hash!;
                        }
                        if (!valid) break;
                        offset += count;
                    }
                    valid &= hashes[^1] == head.BlockHash;
                    if (valid) return hashes;
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { break; }
                catch (Exception exception) when (exception is not OperationCanceledException || !deadline.IsCancellationRequested)
                {
                    if (_logger.IsWarn) _logger.Warn($"Execution ancestor peer {peer.SyncPeer.Node:c} failed: {exception.Message}");
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new RpcException(-32000, "No execution peer supplied an authenticated header chain.");
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

    public Task<Block> GetBlockAsync(BlockHeader header, CancellationToken cancellationToken)
    {
        if (!header.HasBody)
        {
            BlockBody empty = new([], [], header.WithdrawalsRoot is null ? null : []);
            VerifyBlockBody(header, empty);
            return Task.FromResult(new Block(header, empty));
        }
        return RequestExecutionAsync(async (peer, token) =>
        {
            using OwnedBlockBodies bodies = await peer.GetBlockBodies([header.Hash!], token);
            if (bodies.Count != 1 || bodies[0] is not { } body)
                throw new InvalidDataException("Execution peer did not supply a complete block body.");
            VerifyBlockBody(header, body);
            bodies.Disown();
            return new Block(header, body);
        }, cancellationToken);
    }

    internal static void VerifyBlockBody(BlockHeader header, BlockBody body)
    {
        if (!BlockValidator.ValidateTxRootMatchesTxs(header, body, out _)
            || !BlockValidator.ValidateUnclesHashMatches(header, body, out _)
            || !BlockValidator.ValidateWithdrawalsHashMatches(header, body, out _))
            throw new InvalidDataException("Execution peer returned a block body inconsistent with the verified header.");
    }

    public Task<TxReceipt[]> GetReceiptsAsync(Block block, CancellationToken cancellationToken)
    {
        if (block.Transactions.Length == 0)
        {
            VerifyReceipts(block.Header, [], SpecProvider);
            return Task.FromResult(Array.Empty<TxReceipt>());
        }
        return RequestExecutionAsync(async (peer, token) =>
        {
            int count = block.Transactions.Length;
            using IOwnedReadOnlyList<TxReceipt[]?> batches = await peer.GetReceipts([block.Hash!], new[] { count }, token);
            if (batches.Count != 1 || batches[0] is not { } receipts || receipts.Length != count)
                throw new InvalidDataException("Execution peer did not supply the complete receipt list.");
            VerifyReceipts(block.Header, receipts, SpecProvider);
            TxReceipt[] result = new TxReceipt[count];
            for (int i = 0; i < count; i++) result[i] = new TxReceipt(receipts[i]);
            return result;
        }, cancellationToken);
    }

    internal static void VerifyReceipts(BlockHeader header, TxReceipt[] receipts, ISpecProvider specProvider)
    {
        if (ReceiptTrie.CalculateRoot(specProvider.GetSpec(header), receipts, new ReceiptMessageDecoder()) != header.ReceiptsRoot)
            throw new InvalidDataException("Execution peer returned receipts inconsistent with the verified header.");
    }

    private async Task<T> RequestExecutionAsync<T>(Func<ISyncPeer, CancellationToken, Task<T>> request,
        CancellationToken cancellationToken, Func<ISyncPeer, bool>? canServe = null)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await _executionDataRequests.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_stop.IsCancellationRequested)
        {
            throw new RpcException(-32000, "Timed out waiting for an available execution data request slot.");
        }
        try
        {
            ISyncPeerPool pool = _services.Resolve<ISyncPeerPool>();
            while (!deadline.IsCancellationRequested)
            {
                bool hasCapablePeer = false;
                foreach (PeerInfo peer in pool.AllPeers)
                {
                    if (canServe is not null && !canServe(peer.SyncPeer)) continue;
                    hasCapablePeer = true;
                    try
                    {
                        using CancellationTokenSource peerTimeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                        peerTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                        return await request(peer.SyncPeer, peerTimeout.Token);
                    }
                    catch (OperationCanceledException) when (deadline.IsCancellationRequested) { break; }
                    catch (Exception exception) when (exception is not OperationCanceledException || !deadline.IsCancellationRequested)
                    {
                        if (_logger.IsWarn) _logger.Warn($"Execution data peer {peer.SyncPeer.Node:c} failed: {exception.Message}");
                    }
                }
                if (canServe is not null && !hasCapablePeer)
                    throw new NotSupportedException("No execution peer supports the requested data protocol.");
                await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            cancellationToken.ThrowIfCancellationRequested();
            throw new RpcException(-32000, "No execution peer supplied authenticated block data.");
        }
        finally
        {
            _executionDataRequests.Release();
        }
    }

    private async Task<T> RequestAsync<T>(Func<ISnapSyncPeer, CancellationToken, Task<T>> request, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await _snapRequests.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_stop.IsCancellationRequested)
        {
            throw new RpcException(-32000, "Timed out waiting for an available SNAP request slot.");
        }
        try
        {
            return await RequestFromPeerAsync(request, deadline, cancellationToken);
        }
        finally
        {
            _snapRequests.Release();
        }
    }

    private async Task<T> RequestFromPeerAsync<T>(Func<ISnapSyncPeer, CancellationToken, Task<T>> request,
        CancellationTokenSource deadline, CancellationToken cancellationToken)
    {
        ISyncPeerPool pool = _services.Resolve<ISyncPeerPool>();
        while (!deadline.IsCancellationRequested)
        {
            PeerInfo? preferred = Volatile.Read(ref _preferredSnapPeer);
            IEnumerable<PeerInfo> peers = preferred is null ? pool.InitializedPeers : PreferredFirst(preferred, pool.InitializedPeers);
            foreach (PeerInfo peer in peers)
            {
                if (_snapCooldowns.TryGetValue(peer.SyncPeer.Id, out _)) continue;
                if (!peer.SyncPeer.TryGetSatelliteProtocol<ISnapSyncPeer>(Protocol.Snap, out ISnapSyncPeer? snap)) continue;
                try
                {
                    using CancellationTokenSource peerTimeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    peerTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                    T result = await request(snap, peerTimeout.Token);
                    Volatile.Write(ref _preferredSnapPeer, peer);
                    return result;
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !deadline.IsCancellationRequested)
                {
                    _snapCooldowns.Set(peer.SyncPeer.Id, true, new MemoryCacheEntryOptions
                    {
                        Size = 1,
                        AbsoluteExpirationRelativeToNow = exception is InvalidDataException ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(15),
                    });
                    pool.ReportWeakPeer(peer, AllocationContexts.Snap);
                    Interlocked.CompareExchange(ref _preferredSnapPeer, null, peer);
                    if (_logger.IsWarn) _logger.Warn($"SNAP peer {peer.SyncPeer.Node:c} could not serve the selected state: {exception.GetType().Name}: {exception.Message}");
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new RpcException(-32000, "No execution peer supplied a valid SNAP proof for the selected verified state.");
    }

    private static IEnumerable<PeerInfo> PreferredFirst(PeerInfo preferred, IEnumerable<PeerInfo> peers)
    {
        foreach (PeerInfo peer in peers)
            if (ReferenceEquals(peer, preferred)) { yield return peer; break; }
        foreach (PeerInfo peer in peers)
            if (!ReferenceEquals(peer, preferred)) yield return peer;
    }

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
        await _stop.CancelAsync();
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
        _snapCooldowns.Dispose();
        _stop.Dispose();
    }

    private sealed class LightExecutionOverrides(LightSyncServer syncServer, BoundedTransactionRelay transactionRelay,
        CancellationToken stopToken, bool snap2) : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder.RegisterInstance(new ProcessExitSource(stopToken)).As<IProcessExitSource>();
            builder.RegisterInstance(new EthereumJsonSerializer()).As<IJsonSerializer>();
            builder.RegisterInstance(new MemDbFactory()).As<IDbFactory>();
            builder.RegisterInstance(transactionRelay).As<ITxPool>();
            builder.RegisterInstance(LightTxGossipPolicy.Instance).As<ITxGossipPolicySource>();
            builder.RegisterInstance(NoopSnapServer.Instance).As<ISnapServer>();
            builder.RegisterInstance(syncServer).As<ISyncServer>();
            builder.RegisterInstance(new LightGossipPolicy()).As<IGossipPolicy>();
            builder.RegisterType<LightPeerRequestScheduler>().As<IBackgroundTaskScheduler>().SingleInstance();
            builder.RegisterType<ProtocolsManager>()
                .As<IProtocolsManager>()
                .As<IProtocolRegistrar>()
                .WithParameter(new TypedParameter(typeof(IP2PCapabilityResolver[]), new IP2PCapabilityResolver[] { new LightCapabilities(snap2) }))
                .WithAttributeFiltering()
                .SingleInstance();
        }
    }

    private sealed class LightCapabilities(bool snap2) : IP2PCapabilityResolver
    {
        public event Action? Changed { add { } remove { } }
        public void Resolve(ISet<Capability> capabilities)
        {
            for (byte version = 68; version <= 72; version++)
                capabilities.Add(new Capability(Protocol.Eth, version));
            capabilities.Add(new Capability(Protocol.Snap, 1));
            if (snap2) capabilities.Add(new Capability(Protocol.Snap, 2));
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
        private BlockHeader _head;

        internal LightSyncServer(ChainSpec chainSpec, VerifiedHead head, ulong timestamp)
        {
            _chainSpec = chainSpec;
            if (head.Number == 0 && head.BlockHash != chainSpec.Genesis?.Hash)
            {
                // Gloas authenticates the EL hash before its number is known; ETH/69 status must not pair that hash with block zero.
                ulong lastBlockFork = 0;
                foreach (ForkActivation activation in new ChainSpecBasedSpecProvider(chainSpec).TransitionActivations)
                    lastBlockFork = Math.Max(lastBlockFork, activation.BlockNumber);
                head = head with { Number = lastBlockFork };
            }
            _head = CreateHead(head, timestamp);
        }

        internal void UpdateHead(VerifiedHead head, ulong timestamp) => Volatile.Write(ref _head, CreateHead(head, timestamp));

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
        public BlockHeader? FindHeader(Hash256 hash) => hash == Genesis.Hash ? Genesis : null;
        public Hash256? FindHash(ulong number) => number == 0 ? Genesis.Hash : null;
        public IOwnedReadOnlyList<BlockHeader> FindHeaders(Hash256 hash, int numberOfBlocks, int skip, bool reverse) => IOwnedReadOnlyList<BlockHeader>.Empty;
        public IByteArrayList GetNodeData(IReadOnlyList<Hash256> keys, CancellationToken cancellationToken,
            NodeDataType includedTypes = NodeDataType.Code | NodeDataType.State) => EmptyByteArrayList.Instance;
        public int GetPeerCount() => 0;
        public void Dispose() { }
    }
}
