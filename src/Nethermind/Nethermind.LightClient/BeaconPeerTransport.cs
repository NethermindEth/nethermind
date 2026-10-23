// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Net.Quic;
using Nethermind.BeaconChain;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.LightClient.Consensus;
using Nethermind.Libp2p.Core;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using BeaconPeerManager = Nethermind.BeaconChain.P2P.PeerManager;

namespace Nethermind.LightClient;

/// <summary>Obtains light-client SSZ messages directly from connected beacon peers.</summary>
internal sealed class BeaconPeerTransport : IAsyncDisposable
{
    private static readonly TimeSpan PeerRequestTimeout = TimeSpan.FromSeconds(5);
    private readonly IPResolver _ipResolver;
    private readonly BeaconP2P _p2p;
    private readonly BeaconDiscovery _discovery;
    private readonly BeaconPeerManager _peers;
    private readonly LightClientGossip _gossip;
    private readonly Nethermind.Logging.ILogger _logger;
    private readonly PeerRequests _bootstrapRequests;
    private readonly PeerRequests _updateRequests;
    private readonly PeerRequests _optimisticRequests;
    private readonly PeerRequests _finalityRequests;
    private readonly PeerProviders _providers;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private Task? _peerLoop;

    internal int PeerCount => _peers.PeerCount;
    internal Task GossipCompletion => _gossip.Completion;

    internal BeaconPeerTransport(BeaconChainSpec spec, ILogManager logManager, IColumnsDb<BeaconChainDbColumns> db,
        INetworkStorage? peerStorage = null, int p2pPort = 9050, int discoveryPort = 9050,
        TimeProvider? time = null, int quicPort = 9051)
    {
        _logger = logManager.GetClassLogger<BeaconPeerTransport>();
        _time = time ?? TimeProvider.System;
        _providers = new(spec, _time);
        _bootstrapRequests = new(time, _logger, _providers);
        _updateRequests = new(time, _logger, _providers);
        _optimisticRequests = new(time, _logger, _providers);
        _finalityRequests = new(time, _logger, _providers);
        _gossip = new(spec, logManager, time);
        _gossip.ValidatedUpdate = _providers.RecordGossip;
        bool quicSupported = QuicListener.IsSupported && QuicConnection.IsSupported;
        if (!quicSupported && quicPort > 0 && _logger.IsWarn)
            _logger.Warn("QUIC is unavailable on this platform; beacon P2P will use TCP. Install the platform's MsQuic runtime to enable QUIC.");
        BeaconChainConfig config = new()
        {
            P2PPort = p2pPort,
            Discv5Port = discoveryPort,
            QuicPort = quicSupported ? quicPort : 0,
            MinPeerCount = 2,
            TargetPeerCount = 8,
            MaxPeerCount = 12,
        };
        BeaconChainStore store = new(db, spec);
        ITimestamper clock = Timestamper.Default;
        BeaconChainStatusHolder status = new(spec, clock);
        _ipResolver = new IPResolver(new NetworkConfig(), logManager);
        _p2p = new BeaconP2P(config, spec, store, status, new LocalMetadataSource(),
            new DataColumnSidecarPool(), new ExecutionPayloadEnvelopePool(), logManager,
            peerPool: new Lazy<IBeaconSyncPeerPool>(() => _peers!), configureGossip: _gossip.Configure);
        _discovery = new BeaconDiscovery(config, spec, store, _ipResolver, clock, logManager, peerStorage);
        _peers = new BeaconPeerManager(_p2p, config, status, logManager, _discovery, clock);
        _peers.PeerAdmitted += peer =>
        {
            if (_peers.TryGetPeer(PeerProviders.Identity(peer.Id), out PeerRecord record) && record.State == PeerConnectionState.Connected)
                _providers.Admit(peer, record.Direction);
        };
    }

    /// <summary>Opens durable beacon identity and ENR sequence storage owned by the caller.</summary>
    internal static ColumnsDb<BeaconChainDbColumns> OpenPeerDb(string dataDirectory, BeaconChainSpec spec, ILogManager logs)
    {
        string path = Path.Combine(dataDirectory, "cl", spec.ChainId.ToString(), "network");
        DbConfig config = new()
        {
            WriteAheadLogSync = true,
            MaxOpenFiles = 64,
            AdditionalRocksDbOptions = "write_buffer_size=262144;max_write_buffer_number=2;block_based_table_factory.block_cache=262144;target_file_size_base=262144;max_bytes_for_level_base=1048576;",
        };
        ColumnsDb<BeaconChainDbColumns> db = new(path,
            new DbSettings("lightClientBeacon", path) { PersistRepairMarkerUntilAcknowledged = true, SkipMetricsTracking = true },
            config, new RocksDbConfigFactory(config, new PruningConfig(), new HardwareInfo(), logs, validateConfig: false),
            logs, Enum.GetValues<BeaconChainDbColumns>());
        if (db.WasRepairedOnOpen)
        {
            db.Dispose();
            throw new InvalidDataException("Beacon networking storage was repaired; restore it before reusing its identity and ENR sequence.");
        }
        return db;
    }

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        await _p2p.StartAsync(cancellationToken);
        if (_logger.IsInfo) _logger.Info($"Beacon P2P identity: {_p2p.LocalPeerId}");
        await _discovery.Start(cancellationToken);
        _peerLoop = Task.WhenAll(_peers.Run(_stop.Token),
            _peers.DialDiscoveredPeersAsync(_discovery.DiscoverPeers(_stop.Token), _stop.Token),
            MaintainProvidersAsync(_stop.Token));
    }

    private async Task MaintainProvidersAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(15), _time);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                (IBeaconSyncPeer? recycle, string? reconnect) = _providers.Maintain(_peers.Peers);
                if (recycle is not null)
                {
                    if (_logger.IsDebug) _logger.Debug($"Seeking another light-client supplier instead of beacon peer {recycle.Id}");
                    await _peers.RecyclePeerAsync(recycle, "seeking a current light-client data supplier", cancellationToken);
                }
                if (reconnect is not null) await _peers.TryAddPeerAsync(reconnect, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                if (_logger.IsDebug) _logger.Debug($"Light-client supplier search failed: {exception.Message}");
            }
        }
    }

    internal void StartGossip(Func<LightClientFinalityUpdate, CancellationToken, Task<bool>> finality,
        Func<LightClientOptimisticUpdate, CancellationToken, Task<bool>> optimistic, CancellationToken token) =>
        _gossip.Start(_p2p.GetTopic, finality, optimistic, token);

    internal Task<LightClientBootstrap> BootstrapAsync(Hash256 root, Func<LightClientBootstrap, CancellationToken, Task> validate, CancellationToken token) =>
        RequestAsync(_bootstrapRequests, (peer, peerToken) => peer.RequestLightClientBootstrapAsync(root, peerToken), validate, token);

    internal Task<LightClientUpdate> UpdateAsync(ulong period, Func<LightClientUpdate, CancellationToken, Task> validate, CancellationToken token) =>
        RequestAsync(_updateRequests, (peer, peerToken) => peer.RequestLightClientUpdateAsync(period, peerToken), validate, token);

    internal Task<LightClientFinalityUpdate?> PollFinalityAsync(Func<LightClientFinalityUpdate, CancellationToken, Task<bool>> validate, CancellationToken token) =>
        _finalityRequests.PollAsync(() => _peers.GetBestPeers(0), (peer, peerToken) => peer.RequestLightClientFinalityAsync(peerToken), validate, token);

    internal Task<LightClientOptimisticUpdate?> PollOptimisticAsync(Func<LightClientOptimisticUpdate, CancellationToken, Task<bool>> validate, CancellationToken token) =>
        _optimisticRequests.PollAsync(() => _peers.GetBestPeers(0), (peer, peerToken) => peer.RequestLightClientOptimisticAsync(peerToken), validate, token);

    private async Task<T> RequestAsync<T>(PeerRequests requests, Func<IBeaconSyncPeer, CancellationToken, Task<T>> request,
        Func<T, CancellationToken, Task> validate, CancellationToken token) where T : class
    {
        Func<T, CancellationToken, Task<bool>> validateAndAccept = async (response, cancellationToken) =>
        {
            await validate(response, cancellationToken);
            return true;
        };
        long lastStatus = Stopwatch.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            (T? response, _) = await requests.RequestAsync(_peers.GetBestPeers(0), request, validateAndAccept, token);
            if (response is not null) return response;

            if (Stopwatch.GetElapsedTime(lastStatus) >= TimeSpan.FromSeconds(10))
            {
                if (_logger.IsInfo) _logger.Info($"Waiting for beacon light-client data; connected peers: {PeerCount}");
                lastStatus = Stopwatch.GetTimestamp();
            }

            await Task.Delay(TimeSpan.FromSeconds(3), token);
        }
    }

    internal sealed class PeerRequests(TimeProvider? timeProvider = null, Nethermind.Logging.ILogger? logger = null, PeerProviders? providers = null)
    {
        private readonly Nethermind.Logging.ILogger _logger = logger ?? NullLogger.Instance;
        private string? _lastAttemptedPeerId;

        internal async Task<T?> PollAsync<T>(Func<IReadOnlyList<IBeaconSyncPeer>> getPeers,
            Func<IBeaconSyncPeer, CancellationToken, Task<T>> request, Func<T, CancellationToken, Task<bool>> validate,
            CancellationToken token) where T : class
        {
            token.ThrowIfCancellationRequested();
            IReadOnlyList<IBeaconSyncPeer> peers;
            while ((peers = getPeers()).Count == 0)
                await Task.Delay(TimeSpan.FromSeconds(1), timeProvider ?? TimeProvider.System, token);
            (T? response, bool sawInapplicable) = await RequestAsync(peers, request, validate, token);
            if (response is null && !sawInapplicable) throw new IOException("No beacon peer supplied an applicable light-client update.");
            return response;
        }

        internal async Task<(T? Response, bool SawInapplicable)> RequestAsync<T>(IReadOnlyList<IBeaconSyncPeer> peers,
            Func<IBeaconSyncPeer, CancellationToken, Task<T>> request, Func<T, CancellationToken, Task<bool>> validate,
            CancellationToken token) where T : class
        {
            int start = 0;
            for (int i = 0; i < peers.Count; i++)
            {
                if (peers[i].Id == _lastAttemptedPeerId) start = (i + 1) % peers.Count;
            }
            int preferred = providers?.Preferred<T>(peers, start) ?? -1;
            bool sawInapplicable = false;
            for (int offset = preferred >= 0 ? -1 : 0; offset < peers.Count; offset++)
            {
                int index = offset < 0 ? preferred : (start + offset) % peers.Count;
                if (offset >= 0 && index == preferred) continue;
                IBeaconSyncPeer peer = peers[index];
                token.ThrowIfCancellationRequested();
                _lastAttemptedPeerId = peer.Id;
                using CancellationTokenSource peerTimeout = new(PeerRequestTimeout, timeProvider ?? TimeProvider.System);
                using CancellationTokenSource peerAttempt = CancellationTokenSource.CreateLinkedTokenSource(token, peerTimeout.Token);
                T response;
                try
                {
                    response = await request(peer, peerAttempt.Token);
                    if (_logger.IsDebug)
                    {
                        (ulong? Attested, ulong? Signature) slots = response switch
                        {
                            LightClientUpdate update => (update.AttestedHeader?.Beacon?.Slot, update.SignatureSlot),
                            LightClientFinalityUpdate update => (update.AttestedHeader?.Beacon?.Slot, update.SignatureSlot),
                            LightClientOptimisticUpdate update => (update.AttestedHeader?.Beacon?.Slot, update.SignatureSlot),
                            _ => ((ulong?)null, (ulong?)null),
                        };
                        _logger.Debug($"Received {typeof(T).Name} from beacon peer {peer.Id}: attested slot {slots.Attested}, signature slot {slots.Signature}");
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) when (peerTimeout.IsCancellationRequested)
                {
                    providers?.Failed<T>(peer);
                    peer.ReportFailure(PeerFailureReason.RequestFailed, "Light-client request timed out.");
                    continue;
                }
                catch (Exception exception) when (exception is Eth2ReqRespException { ResponseCode: ReqRespFraming.ResponseCode.ResourceUnavailable } ||
                    exception is AggregateException aggregate && aggregate.Flatten().InnerExceptions is [Eth2ReqRespException { ResponseCode: ReqRespFraming.ResponseCode.ResourceUnavailable }])
                {
                    token.ThrowIfCancellationRequested();
                    providers?.Failed<T>(peer);
                    if (_logger.IsDebug) _logger.Debug($"Beacon peer {peer.Id} has no {typeof(T).Name} available");
                    continue;
                }
                catch (Exception exception)
                {
                    providers?.Failed<T>(peer);
                    if (_logger.IsDebug) _logger.Debug($"Beacon {typeof(T).Name} request to {peer.Id} failed: {exception.GetType().Name}: {exception.Message}");
                    peer.ReportFailure(PeerFailureReason.RequestFailed, exception.Message);
                    continue;
                }
                try
                {
                    bool advanced = await validate(response, token);
                    token.ThrowIfCancellationRequested();
                    providers?.RecordValidated(peer, response);
                    if (!advanced)
                    {
                        sawInapplicable = true;
                        if (_logger.IsDebug) _logger.Debug($"Beacon {typeof(T).Name} from {peer.Id} did not advance its requested head");
                        continue;
                    }
                }
                catch (IrrelevantLightClientUpdateException exception)
                {
                    if (_logger.IsDebug) _logger.Debug($"Beacon {typeof(T).Name} from {peer.Id} is outside the current sync period: {exception.Message}");
                    sawInapplicable = true;
                    continue;
                }
                catch (LightClientLocalStateException exception)
                {
                    if (_logger.IsDebug) _logger.Debug($"Beacon {typeof(T).Name} from {peer.Id} cannot yet be applied: {exception.Message}");
                    sawInapplicable = true;
                    continue;
                }
                catch (InvalidDataException exception)
                {
                    providers?.Failed<T>(peer);
                    if (_logger.IsDebug) _logger.Debug($"Beacon {typeof(T).Name} from {peer.Id} failed verification: {exception.Message}");
                    peer.ReportFailure(PeerFailureReason.ProtocolViolation, exception.Message);
                    continue;
                }
                return (response, false);
            }
            return (null, sawInapplicable);
        }
    }

    internal sealed class PeerProviders(BeaconChainSpec spec, TimeProvider time)
    {
        private const int Capacity = 16;
        private const int TargetPeers = 8;
        private const ulong MaxSupplierLag = 10;
        private static readonly TimeSpan AdmissionGrace = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan RedialInterval = TimeSpan.FromSeconds(30);
        private readonly Lock _lock = new();
        private readonly Dictionary<string, Supplier> _sessions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Remembered> _remembered = new(StringComparer.Ordinal);

        internal void Admit(IBeaconSyncPeer peer, PeerDirection direction)
        {
            lock (_lock)
            {
                string id = Identity(peer.Id);
                if (_sessions.TryGetValue(id, out Supplier? current) && ReferenceEquals(current.Peer, peer)) return;
                if (_sessions.Count == Capacity && !_sessions.ContainsKey(id))
                {
                    string? oldest = null;
                    bool oldestUseful = true;
                    long admitted = long.MaxValue;
                    foreach ((string key, Supplier supplier) in _sessions)
                    {
                        bool useful = IsFresh(supplier);
                        if (oldest is null || oldestUseful && !useful || oldestUseful == useful && supplier.Admitted <= admitted)
                        {
                            oldest = key;
                            oldestUseful = useful;
                            admitted = supplier.Admitted;
                        }
                    }
                    _sessions.Remove(oldest!);
                }
                _sessions[id] = new(peer, time.GetTimestamp(), direction == PeerDirection.Outbound);
            }
        }

        internal void RecordValidated<T>(IBeaconSyncPeer peer, T response)
        {
            lock (_lock)
            {
                if (!TryCurrent(peer, out Supplier? supplier)) return;
                switch (response)
                {
                    case LightClientBootstrap:
                        supplier.CandidateAt = time.GetTimestamp();
                        break;
                    case LightClientUpdate update:
                        supplier.CandidateAt = time.GetTimestamp();
                        supplier.UpdateSlot = Math.Max(supplier.UpdateSlot ?? 0, update.AttestedHeader!.Beacon!.Slot);
                        break;
                    case LightClientOptimisticUpdate update:
                        supplier.OptimisticSlot = Math.Max(supplier.OptimisticSlot ?? 0, update.AttestedHeader!.Beacon!.Slot);
                        break;
                    case LightClientFinalityUpdate update:
                        supplier.FinalitySlot = Math.Max(supplier.FinalitySlot ?? 0, update.AttestedHeader!.Beacon!.Slot);
                        break;
                    default:
                        return;
                }
                supplier.FailedProtocols?.Remove(typeof(T));
                if (response is LightClientBootstrap or LightClientUpdate || IsFresh(supplier)) Remember(peer);
            }
        }

        internal void RecordGossip(PeerId source, ulong attestedSlot)
        {
            lock (_lock)
            {
                if (!_sessions.TryGetValue(source.ToString(), out Supplier? supplier)) return;
                supplier.GossipSlot = Math.Max(supplier.GossipSlot ?? 0, attestedSlot);
                if (IsFresh(attestedSlot)) Remember(supplier.Peer);
            }
        }

        internal void Failed<T>(IBeaconSyncPeer peer)
        {
            lock (_lock)
            {
                if (!TryCurrent(peer, out Supplier? supplier)) return;
                (supplier.FailedProtocols ??= []).Add(typeof(T));
                if (typeof(T) == typeof(LightClientOptimisticUpdate)) supplier.OptimisticSlot = null;
                else if (typeof(T) == typeof(LightClientFinalityUpdate)) supplier.FinalitySlot = null;
                else if (typeof(T) == typeof(LightClientUpdate)) supplier.UpdateSlot = null;
            }
        }

        internal int Preferred<T>(IReadOnlyList<IBeaconSyncPeer> peers, int start)
        {
            lock (_lock)
            {
                int preferred = -1, best = 0;
                for (int offset = 0; offset < peers.Count; offset++)
                {
                    int index = (start + offset) % peers.Count;
                    if (!TryCurrent(peers[index], out Supplier? supplier)) continue;
                    if (supplier.FailedProtocols?.Contains(typeof(T)) == true) continue;
                    ulong? slot = typeof(T) == typeof(LightClientOptimisticUpdate) ? supplier.OptimisticSlot
                        : typeof(T) == typeof(LightClientFinalityUpdate) ? supplier.FinalitySlot : supplier.UpdateSlot;
                    int score = IsFresh(slot) ? 2 : IsCandidate(supplier) ? 1 : 0;
                    if (score <= best) continue;
                    best = score;
                    preferred = index;
                }
                return preferred;
            }
        }

        internal (IBeaconSyncPeer? Recycle, string? Reconnect) Maintain(IReadOnlyList<PeerRecord> peers)
        {
            lock (_lock)
            {
                long now = time.GetTimestamp();
                HashSet<string> connected = new(StringComparer.Ordinal);
                HashSet<string> admitted = new(StringComparer.Ordinal);
                foreach (PeerRecord peer in peers)
                {
                    connected.Add(peer.PeerId);
                    if (peer.State == PeerConnectionState.Connected) admitted.Add(peer.PeerId);
                }
                List<string> retired = [];
                foreach ((string id, Supplier supplier) in _sessions)
                    if (!admitted.Contains(id) && time.GetElapsedTime(supplier.Admitted, now) >= AdmissionGrace) retired.Add(id);
                foreach (string id in retired) _sessions.Remove(id);
                retired.Clear();
                foreach ((string id, Remembered remembered) in _remembered)
                    if (time.GetElapsedTime(remembered.LastUseful, now) > Retention) retired.Add(id);
                foreach (string id in retired) _remembered.Remove(id);

                int fresh = 0;
                Supplier? replacement = null;
                foreach (Supplier supplier in _sessions.Values)
                {
                    if (!admitted.Contains(Identity(supplier.Peer.Id))) continue;
                    if (IsFresh(supplier)) { fresh++; continue; }
                    if (time.GetElapsedTime(supplier.Admitted, now) < AdmissionGrace) continue;
                    if (replacement is null || IsCandidate(replacement) && !IsCandidate(supplier) ||
                        IsCandidate(replacement) == IsCandidate(supplier) && supplier.Admitted < replacement.Admitted)
                        replacement = supplier;
                }
                if (fresh >= 2) return (null, null);

                Remembered? reconnect = null;
                foreach ((string id, Remembered remembered) in _remembered)
                {
                    if (connected.Contains(id) || remembered.LastDial is { } dialed && time.GetElapsedTime(dialed, now) < RedialInterval) continue;
                    if (reconnect is null || remembered.LastUseful > reconnect.LastUseful) reconnect = remembered;
                }
                if (reconnect is not null) reconnect.LastDial = now;
                return (admitted.Count >= TargetPeers ? replacement?.Peer : null, reconnect?.Address);
            }
        }

        private bool TryCurrent(IBeaconSyncPeer peer, out Supplier supplier)
        {
            if (_sessions.TryGetValue(Identity(peer.Id), out Supplier? found) && ReferenceEquals(found.Peer, peer))
            {
                supplier = found;
                return true;
            }
            supplier = null!;
            return false;
        }

        private bool IsFresh(Supplier supplier) => IsFresh(supplier.OptimisticSlot) || IsFresh(supplier.FinalitySlot) ||
            IsFresh(supplier.UpdateSlot) || IsFresh(supplier.GossipSlot);

        private bool IsCandidate(Supplier supplier) => supplier.CandidateAt is { } candidate && time.GetElapsedTime(candidate) <= Retention;

        private bool IsFresh(ulong? slot)
        {
            ulong current = spec.GetSlotAtTime((ulong)Math.Max(0, time.GetUtcNow().ToUnixTimeSeconds()));
            return slot is { } attested && attested <= current && current - attested <= MaxSupplierLag;
        }

        private void Remember(IBeaconSyncPeer peer)
        {
            if (!TryCurrent(peer, out Supplier? supplier) || !supplier.CanRedial) return;
            string id = Identity(peer.Id);
            long now = time.GetTimestamp();
            if (_remembered.TryGetValue(id, out Remembered? remembered))
            {
                remembered.Address = peer.Id;
                remembered.LastUseful = now;
                return;
            }
            if (_remembered.Count == Capacity)
            {
                string? oldest = null;
                long useful = long.MaxValue;
                foreach ((string key, Remembered known) in _remembered)
                    if (known.LastUseful <= useful) { oldest = key; useful = known.LastUseful; }
                _remembered.Remove(oldest!);
            }
            _remembered.Add(id, new(peer.Id, now));
        }

        internal static string Identity(string address)
        {
            int index = address.LastIndexOf("/p2p/", StringComparison.Ordinal);
            return index < 0 ? address : address[(index + 5)..];
        }

        private sealed class Supplier(IBeaconSyncPeer peer, long admitted, bool canRedial)
        {
            internal readonly IBeaconSyncPeer Peer = peer;
            internal readonly long Admitted = admitted;
            internal readonly bool CanRedial = canRedial;
            internal long? CandidateAt;
            internal HashSet<Type>? FailedProtocols;
            internal ulong? UpdateSlot;
            internal ulong? OptimisticSlot;
            internal ulong? FinalitySlot;
            internal ulong? GossipSlot;
        }

        private sealed class Remembered(string address, long lastUseful)
        {
            internal string Address = address;
            internal long LastUseful = lastUseful;
            internal long? LastDial;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_peerLoop is not null)
        {
            try { await _peerLoop; }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                if (_logger.IsWarn) _logger.Warn($"Beacon peer loop stopped with an error: {exception.Message}");
            }
        }
        await _discovery.DisposeAsync();
        await _gossip.DisposeAsync();
        await _p2p.DisposeAsync();
        await _ipResolver.DisposeAsync();
        _stop.Dispose();
    }
}
