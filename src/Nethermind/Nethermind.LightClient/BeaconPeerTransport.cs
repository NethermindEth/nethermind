// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Quic;
using System.Text;
using Multiformats.Address;
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
using Nethermind.RocksDbBindings;
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
        _providers = new(spec, _time, _logger, db.GetColumnDb(BeaconChainDbColumns.Metadata));
        _bootstrapRequests = new(time, _logger, _providers);
        _updateRequests = new(time, _logger, _providers);
        _optimisticRequests = new(time, _logger, _providers);
        _finalityRequests = new(time, _logger, _providers);
        _gossip = new(spec, logManager, time);
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
        _gossip.ValidatedUpdate = (source, slot) =>
        {
            _providers.RecordGossip(source, slot);
            if (_logger.IsDebug)
            {
                bool connected = _peers.TryGetPeer(source.ToString(), out PeerRecord record) && record.State == PeerConnectionState.Connected;
                _logger.Debug($"Authenticated light-client gossip supplier {source}: attested slot {slot}, connected {connected}, direction {(connected ? record.Direction : (PeerDirection?)null)}, address {(connected ? record.LastKnownMultiaddr : null)}, agent {(connected ? record.AgentVersion : null)}");
            }
        };
        _peers.PeerAdmitted += peer =>
        {
            if (_peers.TryGetPeer(PeerProviders.Identity(peer.Id), out PeerRecord record) && record.State == PeerConnectionState.Connected)
                _providers.Admit(peer, record.Direction);
            else if (_logger.IsDebug) _logger.Debug($"Admitted beacon peer {peer.Id} no longer has a connected peer-directory record");
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
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            _providers.FlushHints();
            try
            {
                (IBeaconSyncPeer? recycle, string? reconnect) = _providers.Maintain(_peers.Peers);
                if (recycle is not null)
                {
                    if (_logger.IsDebug) _logger.Debug($"Seeking another light-client supplier instead of beacon peer {recycle.Id}");
                    await _peers.RecyclePeerAsync(recycle, "seeking a current light-client data supplier", cancellationToken);
                }
                if (reconnect is not null)
                {
                    if (_logger.IsDebug) _logger.Debug($"Redialing remembered light-client supplier {reconnect}");
                    bool admitted = await _peers.TryAddPeerAsync(reconnect, cancellationToken, allowAboveTarget: true);
                    if (_logger.IsDebug) _logger.Debug($"Remembered light-client supplier redial {reconnect}: admitted {admitted}");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                if (_logger.IsDebug) _logger.Debug($"Light-client supplier search failed: {exception.Message}");
            }
        } while (await timer.WaitForNextTickAsync(cancellationToken));
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

    internal sealed class PeerProviders(BeaconChainSpec spec, TimeProvider time, Nethermind.Logging.ILogger? logger = null, IDb? metadata = null)
    {
        private const int Capacity = 16;
        private const int TargetPeers = 8;
        private const ulong MaxSupplierLag = 10;
        private static readonly TimeSpan AdmissionGrace = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan HintRetention = TimeSpan.FromHours(24);
        private static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan RedialInterval = TimeSpan.FromSeconds(30);
        private const int MaxAddressBytes = 1024;
        private const int HintHeaderBytes = 37;
        private const int MaxHintBytes = HintHeaderBytes + Capacity * (sizeof(long) + sizeof(ushort) + MaxAddressBytes);
        private static ReadOnlySpan<byte> HintKey => "light-client-supplier-hints/v1"u8;
        private static readonly UTF8Encoding HintEncoding = new(false, true);
        private readonly Nethermind.Logging.ILogger _logger = logger ?? NullLogger.Instance;
        private readonly Lock _lock = new();
        private readonly Dictionary<string, Supplier> _sessions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Remembered> _remembered = LoadHints(metadata, spec, time, logger ?? NullLogger.Instance);
        private long _hintGeneration;
        private long _savedHintGeneration;
        private long? _lastSaveAttempt;
        private bool _stopping;

        internal void Admit(IBeaconSyncPeer peer, PeerDirection direction)
        {
            string id = Identity(peer.Id);
            string? evicted = null;
            bool replaced;
            lock (_lock)
            {
                if (_sessions.TryGetValue(id, out Supplier? current) && ReferenceEquals(current.Peer, peer)) return;
                replaced = current is not null;
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
                    evicted = oldest;
                }
                _sessions[id] = new(peer, time.GetTimestamp(), direction == PeerDirection.Outbound);
            }
            if (_logger.IsDebug) _logger.Debug($"Tracking light-client supplier {id}: address {peer.Id}, direction {direction}, replaced session {replaced}, capacity eviction {evicted}");
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
            bool tracked = false;
            ulong? recordedSlot = null;
            string? trackedAddress = null;
            string? rememberedAddress = null;
            lock (_lock)
            {
                if (_sessions.TryGetValue(source.ToString(), out Supplier? supplier))
                {
                    tracked = true;
                    supplier.GossipSlot = Math.Max(supplier.GossipSlot ?? 0, attestedSlot);
                    if (IsFresh(attestedSlot)) Remember(supplier.Peer);
                    recordedSlot = supplier.GossipSlot;
                    trackedAddress = supplier.Peer.Id;
                    if (_logger.IsDebug && _remembered.TryGetValue(source.ToString(), out Remembered? remembered)) rememberedAddress = remembered.Address;
                }
            }
            if (_logger.IsDebug) _logger.Debug($"Light-client gossip supplier credit {source}: attested slot {attestedSlot}, tracked {tracked}, tracked address {trackedAddress}, recorded gossip slot {recordedSlot}, remembered address {rememberedAddress}");
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
            int connectedCount, occupiedCount, trackedCount, fresh, rememberedCount;
            List<string>? retiredSessions = null;
            IBeaconSyncPeer? recycle = null;
            string? redialAddress = null;
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
                if (_logger.IsDebug && retired.Count > 0) retiredSessions = [.. retired];
                retired.Clear();
                foreach ((string id, Remembered remembered) in _remembered)
                    if (HintAge(remembered, now) > HintRetention) retired.Add(id);
                foreach (string id in retired) _remembered.Remove(id);
                if (retired.Count > 0) _hintGeneration++;

                fresh = 0;
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
                if (fresh < 2)
                {
                    Remembered? reconnect = null;
                    foreach ((string id, Remembered remembered) in _remembered)
                    {
                        if (connected.Contains(id) || remembered.LastDial is { } dialed && time.GetElapsedTime(dialed, now) < RedialInterval) continue;
                        if (reconnect is null || remembered.LastDial is null && reconnect.LastDial is not null ||
                            remembered.LastDial is { } last && reconnect.LastDial is { } previous && last < previous ||
                            remembered.LastDial == reconnect.LastDial && HintAge(remembered, now) < HintAge(reconnect, now))
                            reconnect = remembered;
                    }
                    if (reconnect is not null) reconnect.LastDial = now;
                    recycle = connected.Count >= TargetPeers ? replacement?.Peer : null;
                    redialAddress = reconnect?.Address;
                }
                connectedCount = admitted.Count;
                occupiedCount = connected.Count;
                trackedCount = _sessions.Count;
                rememberedCount = _remembered.Count;
            }
            if (_logger.IsDebug)
            {
                if (retiredSessions is not null)
                    foreach (string id in retiredSessions) _logger.Debug($"Retired disconnected light-client supplier session {id}");
                _logger.Debug($"Light-client supplier maintenance: connected {connectedCount}, occupied {occupiedCount}, tracked {trackedCount}, fresh {fresh}, remembered {rememberedCount}, recycle {recycle?.Id}, reconnect {redialAddress}");
            }
            return (recycle, redialAddress);
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
            if (_stopping || !TryCurrent(peer, out Supplier? supplier) || !supplier.CanRedial || peer.Id.Length > MaxAddressBytes) return;
            string id = Identity(peer.Id);
            long now = time.GetTimestamp();
            long utc = time.GetUtcNow().ToUnixTimeSeconds();
            _hintGeneration++;
            if (_remembered.TryGetValue(id, out Remembered? remembered))
            {
                remembered.Address = peer.Id;
                remembered.LastUseful = now;
                remembered.UsefulUtc = utc;
                remembered.InitialAge = TimeSpan.Zero;
                remembered.LastDial = null;
                return;
            }
            if (_remembered.Count == Capacity)
            {
                string? oldest = null;
                TimeSpan oldestAge = TimeSpan.MinValue;
                foreach ((string key, Remembered known) in _remembered)
                    if (HintAge(known, now) >= oldestAge) { oldest = key; oldestAge = HintAge(known, now); }
                _remembered.Remove(oldest!);
            }
            _remembered.Add(id, new(peer.Id, now, utc));
        }

        private TimeSpan HintAge(Remembered remembered, long now) => remembered.InitialAge + time.GetElapsedTime(remembered.LastUseful, now);

        internal void FlushHints(bool stopping = false)
        {
            List<(string Address, long UsefulUtc)> snapshot;
            long generation;
            lock (_lock)
            {
                if (_stopping) return;
                if (stopping) _stopping = true;
                if (metadata is null || _savedHintGeneration == _hintGeneration) return;
                long now = time.GetTimestamp();
                if (!stopping && _lastSaveAttempt is { } previous && time.GetElapsedTime(previous, now) < SaveInterval) return;
                _lastSaveAttempt = now;
                generation = _hintGeneration;
                snapshot = new(_remembered.Count);
                foreach (Remembered remembered in _remembered.Values)
                    if (HintAge(remembered, now) <= HintRetention) snapshot.Add((remembered.Address, remembered.UsefulUtc));
            }
            byte[] encoded = EncodeHints(snapshot, spec);
            try { metadata.Set(HintKey, encoded); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or RocksDbException)
            {
                if (_logger.IsWarn) _logger.Warn($"Saving optional light-client supplier hints failed: {exception.GetType().Name}");
                return;
            }
            lock (_lock) _savedHintGeneration = generation;
        }

        private static Dictionary<string, Remembered> LoadHints(IDb? db, BeaconChainSpec spec, TimeProvider time, Nethermind.Logging.ILogger logger)
        {
            Dictionary<string, Remembered> result = new(StringComparer.Ordinal);
            if (db is null) return result;
            try
            {
                using MemoryManager<byte>? saved = db.GetOwnedMemory(HintKey);
                if (saved is null) return result;
                ReadOnlySpan<byte> bytes = saved.Memory.Span;
                if (bytes.Length is < HintHeaderBytes or > MaxHintBytes || !bytes[..4].SequenceEqual("LCP1"u8) ||
                    !bytes.Slice(4, Hash256.Size).SequenceEqual(spec.GenesisValidatorsRoot.Bytes) || bytes[36] > Capacity)
                    throw new InvalidDataException("Invalid supplier hint header.");
                long utc = time.GetUtcNow().ToUnixTimeSeconds();
                long now = time.GetTimestamp();
                int offset = HintHeaderBytes;
                for (int i = 0; i < bytes[36]; i++)
                {
                    if (bytes.Length - offset < sizeof(long) + sizeof(ushort)) throw new InvalidDataException("Truncated supplier hint.");
                    long usefulUtc = BinaryPrimitives.ReadInt64BigEndian(bytes[offset..]);
                    int length = BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + sizeof(long))..]);
                    offset += sizeof(long) + sizeof(ushort);
                    if (length is 0 or > MaxAddressBytes || bytes.Length - offset < length) throw new InvalidDataException("Invalid supplier hint address length.");
                    ReadOnlySpan<byte> address = bytes.Slice(offset, length);
                    offset += length;
                    if (usefulUtc < 0 || usefulUtc > utc || utc - usefulUtc > HintRetention.TotalSeconds ||
                        !TryCanonicalAddress(HintEncoding.GetString(address), out string canonical)) continue;
                    string id = Identity(canonical);
                    if (!result.TryGetValue(id, out Remembered? previous) || usefulUtc > previous.UsefulUtc ||
                        usefulUtc == previous.UsefulUtc && string.CompareOrdinal(canonical, previous.Address) < 0)
                        result[id] = new(canonical, now, usefulUtc, TimeSpan.FromSeconds(utc - usefulUtc));
                }
                if (offset != bytes.Length) throw new InvalidDataException("Trailing supplier hint data.");
                return result;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or RocksDbException or DecoderFallbackException)
            {
                if (logger.IsWarn) logger.Warn($"Ignoring optional light-client supplier hints: {exception.GetType().Name}");
                return new(StringComparer.Ordinal);
            }
        }

        private static byte[] EncodeHints(List<(string Address, long UsefulUtc)> snapshot, BeaconChainSpec spec)
        {
            List<(byte[] Address, long UsefulUtc)> entries = new(snapshot.Count);
            int length = HintHeaderBytes;
            foreach ((string address, long usefulUtc) in snapshot)
            {
                if (usefulUtc < 0 || !TryCanonicalAddress(address, out string canonical)) continue;
                byte[] bytes = HintEncoding.GetBytes(canonical);
                entries.Add((bytes, usefulUtc));
                length += sizeof(long) + sizeof(ushort) + bytes.Length;
            }
            byte[] encoded = new byte[length];
            "LCP1"u8.CopyTo(encoded);
            spec.GenesisValidatorsRoot.Bytes.CopyTo(encoded.AsSpan(4));
            encoded[36] = (byte)entries.Count;
            int offset = HintHeaderBytes;
            foreach ((byte[] address, long usefulUtc) in entries)
            {
                BinaryPrimitives.WriteInt64BigEndian(encoded.AsSpan(offset), usefulUtc);
                BinaryPrimitives.WriteUInt16BigEndian(encoded.AsSpan(offset + sizeof(long)), (ushort)address.Length);
                offset += sizeof(long) + sizeof(ushort);
                address.CopyTo(encoded.AsSpan(offset));
                offset += address.Length;
            }
            return encoded;
        }

        private static bool TryCanonicalAddress(string address, out string canonical)
        {
            canonical = string.Empty;
            if (address.Length > MaxAddressBytes) return false;
            try
            {
                string[] parts = address.Split('/');
                bool tcp = parts is ["", "ip4" or "ip6" or "dns" or "dns4" or "dns6", _, "tcp", _, "p2p", _];
                bool quic = parts is ["", "ip4" or "ip6", _, "udp", _, "quic-v1", "p2p", _];
                if (!(tcp || quic) || !ushort.TryParse(parts[4], out ushort port) || port == 0) return false;
                Multiaddress decoded = Multiaddress.Decode(address);
                if (decoded.GetPeerId() is not { } id || Identity(address) != id.ToString()) return false;
                canonical = decoded.ToString();
                return HintEncoding.GetByteCount(canonical) <= MaxAddressBytes;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException) { return false; }
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

        private sealed class Remembered(string address, long lastUseful, long usefulUtc, TimeSpan initialAge = default)
        {
            internal string Address = address;
            internal long LastUseful = lastUseful;
            internal long UsefulUtc = usefulUtc;
            internal TimeSpan InitialAge = initialAge;
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
        try
        {
            await _discovery.DisposeAsync();
            await _gossip.DisposeAsync();
            await _p2p.DisposeAsync();
            await _ipResolver.DisposeAsync();
        }
        finally
        {
            try { _providers.FlushHints(stopping: true); }
            finally { _stop.Dispose(); }
        }
    }
}
