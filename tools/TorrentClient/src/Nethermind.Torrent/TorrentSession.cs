// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Nethermind.Torrent;

/// <summary>
/// Options for running the standalone torrent client.
/// </summary>
public sealed class TorrentClientOptions
{
    /// <summary>
    /// Gets or sets the path to the `.torrent` file.
    /// </summary>
    public required string TorrentPath { get; init; }

    /// <summary>
    /// Gets or sets the directory where payload files are written.
    /// </summary>
    public required string OutputDirectory { get; init; }

    /// <summary>
    /// Gets or sets the TCP listening port. Zero selects an available ephemeral port when seeding is enabled.
    /// </summary>
    public int ListenPort { get; init; } = 6881;

    /// <summary>
    /// Gets or sets the maximum number of concurrent peer connections.
    /// </summary>
    public int MaxPeers { get; init; } = 32;

    /// <summary>Gets or sets the maximum number of concurrent inbound and outbound upload connections.</summary>
    public int MaxUploadPeers { get; init; } = 8;

    /// <summary>Gets or sets whether verified pieces are shared while downloading and after completion.</summary>
    public bool SeedAfterCompletion { get; init; } = true;

    /// <summary>
    /// Gets or sets whether the DHT fallback should run in addition to tracker announces.
    /// </summary>
    public bool EnableDht { get; init; } = true;

    /// <summary>
    /// Gets or sets whether HTTP and UDP trackers should be queried.
    /// </summary>
    public bool EnableTrackers { get; init; } = true;

    /// <summary>Gets explicit peer endpoints to try before tracker or DHT discovery.</summary>
    public IReadOnlyList<string> ExplicitPeers { get; init; } = [];

    /// <summary>
    /// Gets or sets whether existing payload files should be SHA-1 verified before downloading.
    /// </summary>
    public bool VerifyExistingData { get; init; } = true;

    /// <summary>
    /// Gets or sets the HTTP and UDP tracker request timeout.
    /// </summary>
    public TimeSpan TrackerTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Gets or sets how long the DHT fallback is allowed to search for peers.
    /// </summary>
    public TimeSpan DhtLookupTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gets or sets how often DHT fallback peer searches are attempted while no peers are active.
    /// </summary>
    public TimeSpan DhtLookupInterval { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Gets or sets the peer-wire read and useful-progress timeout.
    /// </summary>
    public TimeSpan PeerTimeout { get; init; } = TimeSpan.FromSeconds(45);
}

/// <summary>
/// Runs tracker, DHT, peer-wire, storage, and piece verification for one torrent.
/// </summary>
public sealed class TorrentSession(TorrentClientOptions options, Action<string>? log = null, IProgress<TorrentSessionProgress>? progress = null)
{
    private const int MaxPeerConnections = 512;
    private const int MaxUploadConnections = 64;

    private readonly TorrentClientOptions _options = ValidateOptions(options);
    private readonly Action<string> _log = log ?? Console.WriteLine;
    private readonly IProgress<TorrentSessionProgress>? _progress = progress;
    private readonly byte[] _peerId = CreatePeerId();
    private readonly string _trackerKey = RandomNumberGenerator.GetHexString(8, lowercase: true);
    private int _activePeerCount;
    private int _knownPeerCount;
    private long _payloadBytesReceived;
    private long _verifiedBytesFromPeers;
    private long _uploadedBytes;
    private long _activeStartTimestamp;
    private long _activeEndTimestamp;
    private readonly Lock _contributorsLock = new();
    private readonly HashSet<string> _contributors = [];
    private PeerSeedServer? _seedServer;
    private TorrentSessionProgress? _latestProgress;

    /// <summary>Gets the active inbound peer-wire port, or zero when not listening.</summary>
    public int ListeningPort => Volatile.Read(ref _seedServer)?.Port ?? 0;

    /// <summary>Gets the latest engine progress, including updates not yet dispatched to a UI callback.</summary>
    public TorrentSessionProgress? GetLatestProgress() => Volatile.Read(ref _latestProgress);

    /// <summary>Gets network transfer activity measured during this session.</summary>
    public TorrentTransferSnapshot GetTransferSnapshot()
    {
        long start = Volatile.Read(ref _activeStartTimestamp);
        long end = Volatile.Read(ref _activeEndTimestamp);
        TimeSpan activeTime = start == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(start, end == 0 ? Stopwatch.GetTimestamp() : end);
        lock (_contributorsLock)
        {
            (long received, long verified) = ReadTransferCounters(
                () => Interlocked.Read(ref _verifiedBytesFromPeers),
                () => Interlocked.Read(ref _payloadBytesReceived));
            return new TorrentTransferSnapshot(received, verified, activeTime, _contributors.Count,
                Interlocked.Read(ref _uploadedBytes), Volatile.Read(ref _seedServer)?.ActivePeers ?? 0);
        }
    }

    internal static (long Received, long Verified) ReadTransferCounters(Func<long> readVerified, Func<long> readReceived)
    {
        long verified = readVerified();
        long received = readReceived();
        return (received, verified);
    }

    /// <summary>
    /// Loads metadata, downloads and verifies pieces, then shares them until cancellation when seeding is enabled.
    /// </summary>
    /// <param name="token">Token used to cancel the torrent session.</param>
    /// <returns>The loaded torrent metadata.</returns>
    public async Task<TorrentMetadata> RunAsync(CancellationToken token)
    {
        _log($"loading torrent metadata: {_options.TorrentPath}");
        ReportProgress(TorrentSessionPhase.LoadingMetadata, null, null, "Loading torrent metadata");
        TorrentMetadata torrent = TorrentMetadata.Load(_options.TorrentPath);
        _log($"torrent: {torrent.Name}");
        _log($"info hash: {torrent.InfoHashHex}");
        _log($"payload: {FormatBytes(torrent.TotalLength)}, pieces: {torrent.PieceCount}, piece length: {FormatBytes(torrent.PieceLength)}");
        bool useDht = _options.EnableDht && !torrent.IsPrivate;
        if (_options.EnableDht && torrent.IsPrivate)
        {
            _log("private torrent: public DHT discovery and announcements disabled");
        }

        ReportProgress(TorrentSessionPhase.LoadingMetadata, torrent, null, "Metadata loaded");

        await using TorrentStorage storage = new(torrent, _options.OutputDirectory);
        _log($"initializing storage: {_options.OutputDirectory}");
        ReportProgress(TorrentSessionPhase.InitializingStorage, torrent, null, "Initializing storage");
        await storage.InitializeAsync(token);
        _log("storage ready");
        ReportProgress(TorrentSessionPhase.InitializingStorage, torrent, null, "Storage ready");

        PiecePicker picker = new(torrent);
        if (_options.VerifyExistingData)
        {
            await VerifyExistingPiecesAsync(torrent, storage, picker, token);
        }

        bool wasCompleteAtStartup = picker.IsComplete;
        if (!wasCompleteAtStartup && (!_options.EnableTrackers || torrent.Trackers.Count == 0) && !useDht &&
            (_options.ExplicitPeers.Count == 0 || torrent.IsPrivate))
        {
            throw new InvalidOperationException("Incomplete payload requires a tracker, DHT, or explicit peer.");
        }

        await using PeerSeedServer? seedServer = _options.SeedAfterCompletion
            ? new PeerSeedServer(torrent, _peerId, picker, storage, _options.ListenPort, _options.MaxUploadPeers,
                _options.PeerTimeout, length => Interlocked.Add(ref _uploadedBytes, length), _log)
            : null;
        seedServer?.Start();
        Volatile.Write(ref _seedServer, seedServer);
        int announcePort = seedServer?.Port ?? _options.ListenPort;

        using HttpClient httpClient = new()
        {
            Timeout = _options.TrackerTimeout,
        };

        TrackerClient trackerClient = new(httpClient, message => _log(message), _options.TrackerTimeout);
        DhtClient? dhtClient = null;
        if (useDht)
        {
            dhtClient = new DhtClient(_peerId, message => _log(message));
        }

        bool completed = false;
        Volatile.Write(ref _activeStartTimestamp, Stopwatch.GetTimestamp());
        try
        {
            completed = await DownloadAsync(torrent, storage, picker, trackerClient, dhtClient, seedServer, announcePort, token);
            Volatile.Write(ref _activeEndTimestamp, Stopwatch.GetTimestamp());
            if (completed)
            {
                TimeSpan trackerInterval = TimeSpan.FromMinutes(15);
                IReadOnlyList<PeerEndpoint> initialSeedPeers = [];
                if (_options.EnableTrackers)
                {
                    TrackerAnnounceResult announce = await trackerClient.AnnounceAsync(torrent, _peerId,
                        _trackerKey, announcePort, picker.DownloadedBytes, Interlocked.Read(ref _uploadedBytes), token);
                    trackerInterval = ClampTrackerInterval(announce.Interval);
                    initialSeedPeers = announce.Peers;
                    if (!wasCompleteAtStartup)
                    {
                        await trackerClient.AnnounceEventAsync(
                            torrent,
                            _peerId,
                            _trackerKey,
                            announcePort,
                            picker.DownloadedBytes,
                            uploaded: Interlocked.Read(ref _uploadedBytes),
                            "completed",
                            token);
                    }
                }

                ReportProgress(TorrentSessionPhase.Completed, torrent, picker, "Torrent complete");
                if (seedServer is not null)
                {
                    ReportProgress(TorrentSessionPhase.Seeding, torrent, picker, "Seeding verified content");
                    try
                    {
                        await SeedAsync(torrent, picker, trackerClient, dhtClient, seedServer, initialSeedPeers,
                            announcePort, trackerInterval, token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        _log("seeding stopped");
                    }
                }
            }
        }
        finally
        {
            if (Volatile.Read(ref _activeEndTimestamp) == 0)
            {
                Volatile.Write(ref _activeEndTimestamp, Stopwatch.GetTimestamp());
            }

            if (seedServer is not null)
            {
                await seedServer.StopAsync();
                Volatile.Write(ref _seedServer, null);
            }

            using CancellationTokenSource stoppedCts = new(
                _options.TrackerTimeout < TimeSpan.FromSeconds(5) ? _options.TrackerTimeout : TimeSpan.FromSeconds(5));
            if (_options.EnableTrackers)
            {
                try
                {
                    await trackerClient.AnnounceEventAsync(
                        torrent,
                        _peerId,
                        _trackerKey,
                        announcePort,
                        picker.DownloadedBytes,
                        uploaded: Interlocked.Read(ref _uploadedBytes),
                        "stopped",
                        stoppedCts.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }

            if (dhtClient is not null)
            {
                await dhtClient.DisposeAsync();
            }

        }

        _log($"complete: {Path.GetFullPath(Path.Combine(_options.OutputDirectory, torrent.Name))}");
        return torrent;
    }

    private async Task<bool> DownloadAsync(
        TorrentMetadata torrent,
        TorrentStorage storage,
        PiecePicker picker,
        TrackerClient trackerClient,
        DhtClient? dhtClient,
        PeerSeedServer? seedServer,
        int announcePort,
        CancellationToken token)
    {
        Dictionary<Task, PeerEndpoint> activePeers = [];
        HashSet<PeerEndpoint> recentlyFailed = [];
        HashSet<PeerEndpoint> knownPeers = [];
        for (int i = 0; !torrent.IsPrivate && i < _options.ExplicitPeers.Count; i++)
        {
            _ = MagnetLink.TryParsePeer(_options.ExplicitPeers[i], out PeerEndpoint peer);
            knownPeers.Add(peer);
        }

        Volatile.Write(ref _knownPeerCount, knownPeers.Count);
        DateTimeOffset nextAnnounce = DateTimeOffset.MinValue;
        DateTimeOffset lastDht = DateTimeOffset.MinValue;
        DateTimeOffset nextDhtAnnounce = DateTimeOffset.MinValue;
        PeerWireClient peerWire = new(
            torrent,
            _peerId,
            picker,
            storage,
            _log,
            _options.PeerTimeout,
            (pieceIndex, peer, remotePeerId) =>
            {
                lock (_contributorsLock)
                {
                    _contributors.Add(remotePeerId);
                    Interlocked.Add(ref _verifiedBytesFromPeers, torrent.GetPieceSize(pieceIndex));
                }

                seedServer?.PieceVerified(pieceIndex);

                ReportProgress(TorrentSessionPhase.Downloading, torrent, picker,
                    $"Piece {pieceIndex + 1}/{torrent.PieceCount} from {peer}");
            },
            blockLength => Interlocked.Add(ref _payloadBytesReceived, blockLength));
        using CancellationTokenSource peerCts = CancellationTokenSource.CreateLinkedTokenSource(token);

        try
        {
            if (!picker.IsComplete && knownPeers.Count > 0)
            {
                StartPeerWorkers(torrent, peerWire, knownPeers, recentlyFailed, activePeers, peerCts.Token);
                Volatile.Write(ref _activePeerCount, activePeers.Count);
            }

            while (!picker.IsComplete)
            {
                token.ThrowIfCancellationRequested();
                if (_options.EnableTrackers && DateTimeOffset.UtcNow >= nextAnnounce)
                {
                    ReportProgress(TorrentSessionPhase.DiscoveringPeers, torrent, picker, "Announcing to trackers");
                    TrackerAnnounceResult trackerResult = await trackerClient.AnnounceAsync(
                        torrent,
                        _peerId,
                        _trackerKey,
                        announcePort,
                        picker.DownloadedBytes,
                        uploaded: Interlocked.Read(ref _uploadedBytes),
                        token);
                    AddKnownPeers(knownPeers, trackerResult.Peers);
                    Volatile.Write(ref _knownPeerCount, knownPeers.Count);
                    nextAnnounce = DateTimeOffset.UtcNow + ClampTrackerInterval(trackerResult.Interval);
                    _log($"tracker peers: {knownPeers.Count}");
                    ReportProgress(TorrentSessionPhase.DiscoveringPeers, torrent, picker, $"Tracker peers: {knownPeers.Count}");
                }

                if (dhtClient is not null && seedServer is not null && DateTimeOffset.UtcNow >= nextDhtAnnounce)
                {
                    await AnnounceDhtAsync(dhtClient, torrent, announcePort, isSeed: false, token);
                    nextDhtAnnounce = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(15);
                }

                StartPeerWorkers(torrent, peerWire, knownPeers, recentlyFailed, activePeers, peerCts.Token);
                Volatile.Write(ref _activePeerCount, activePeers.Count);

                if (activePeers.Count == 0 && dhtClient is not null && DateTimeOffset.UtcNow - lastDht > _options.DhtLookupInterval)
                {
                    try
                    {
                        ReportProgress(TorrentSessionPhase.DiscoveringPeers, torrent, picker, "Querying DHT");
                        using CancellationTokenSource dhtCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        dhtCts.CancelAfter(_options.DhtLookupTimeout);
                        IReadOnlyList<PeerEndpoint> dhtPeers = await dhtClient.FindPeersAsync(torrent.InfoHash, dhtCts.Token);
                        AddKnownPeers(knownPeers, dhtPeers);
                        Volatile.Write(ref _knownPeerCount, knownPeers.Count);
                        ReportProgress(TorrentSessionPhase.DiscoveringPeers, torrent, picker, $"Known peers: {knownPeers.Count}");
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (OperationCanceledException)
                    {
                        _log("dht peer lookup timed out");
                    }
                    catch (Exception exception)
                    {
                        _log($"dht peer lookup failed: {exception.Message}");
                    }

                    lastDht = DateTimeOffset.UtcNow;
                    StartPeerWorkers(torrent, peerWire, knownPeers, recentlyFailed, activePeers, peerCts.Token);
                    Volatile.Write(ref _activePeerCount, activePeers.Count);
                }

                if (activePeers.Count == 0)
                {
                    _log("no active peers; waiting before another announce");
                    ReportProgress(TorrentSessionPhase.DiscoveringPeers, torrent, picker, "Waiting for peers");
                    await Task.Delay(TimeSpan.FromSeconds(10), token);
                    recentlyFailed.Clear();
                    continue;
                }

                Task finished = await Task.WhenAny(activePeers.Keys);
                PeerEndpoint peer = activePeers[finished];
                activePeers.Remove(finished);
                Volatile.Write(ref _activePeerCount, activePeers.Count);
                try
                {
                    await finished;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException) when (peerCts.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    recentlyFailed.Add(peer);
                    _log($"peer {peer} failed: {exception.Message}");
                }
            }

            return true;
        }
        finally
        {
            await peerCts.CancelAsync();
            Volatile.Write(ref _activePeerCount, 0);
            foreach ((Task task, PeerEndpoint peer) in activePeers)
            {
                try
                {
                    await task;
                }
                catch (OperationCanceledException) when (peerCts.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    _log($"peer {peer} shutdown failed: {exception.Message}");
                }
            }
        }
    }

    private async Task SeedAsync(
        TorrentMetadata torrent,
        PiecePicker picker,
        TrackerClient trackerClient,
        DhtClient? dhtClient,
        PeerSeedServer seedServer,
        IReadOnlyList<PeerEndpoint> initialPeers,
        int announcePort,
        TimeSpan trackerInterval,
        CancellationToken token)
    {
        HashSet<PeerEndpoint> knownPeers = [];
        Queue<PeerEndpoint> peerOrder = [];
        Dictionary<PeerEndpoint, DateTimeOffset> nextConnectAttempt = [];
        List<PeerEndpoint> explicitPeers = [];
        for (int i = 0; !torrent.IsPrivate && i < _options.ExplicitPeers.Count; i++)
        {
            _ = MagnetLink.TryParsePeer(_options.ExplicitPeers[i], out PeerEndpoint peer);
            explicitPeers.Add(peer);
        }

        AddSeedPeers(knownPeers, peerOrder, nextConnectAttempt, explicitPeers);
        AddSeedPeers(knownPeers, peerOrder, nextConnectAttempt, initialPeers);
        DateTimeOffset nextTrackerAnnounce = DateTimeOffset.UtcNow + trackerInterval;
        DateTimeOffset nextDhtAnnounce = DateTimeOffset.MinValue;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (_options.EnableTrackers && now >= nextTrackerAnnounce)
            {
                TrackerAnnounceResult announce = await trackerClient.AnnounceAsync(
                    torrent, _peerId, _trackerKey, announcePort, picker.DownloadedBytes,
                    Interlocked.Read(ref _uploadedBytes), token);
                AddSeedPeers(knownPeers, peerOrder, nextConnectAttempt, announce.Peers);
                nextTrackerAnnounce = DateTimeOffset.UtcNow + ClampTrackerInterval(announce.Interval);
            }

            ConnectSeedPeers(seedServer, knownPeers, nextConnectAttempt);

            if (dhtClient is not null && now >= nextDhtAnnounce)
            {
                try
                {
                    using CancellationTokenSource lookupCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    lookupCts.CancelAfter(_options.DhtLookupTimeout);
                    IReadOnlyList<PeerEndpoint> dhtPeers = await dhtClient.FindPeersAsync(torrent.InfoHash, lookupCts.Token);
                    AddSeedPeers(knownPeers, peerOrder, nextConnectAttempt, dhtPeers);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    _log("dht seed peer lookup timed out");
                }
                catch (Exception exception)
                {
                    _log($"dht seed peer lookup failed: {exception.Message}");
                }

                ConnectSeedPeers(seedServer, knownPeers, nextConnectAttempt);
                await AnnounceDhtAsync(dhtClient, torrent, announcePort, isSeed: true, token);
                nextDhtAnnounce = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(15);
            }

            Volatile.Write(ref _knownPeerCount, knownPeers.Count);
            ReportProgress(TorrentSessionPhase.Seeding, torrent, picker, "Seeding verified content");
            await Task.Delay(TimeSpan.FromSeconds(5), token);
        }
    }

    internal static void AddSeedPeers(HashSet<PeerEndpoint> knownPeers, Queue<PeerEndpoint> peerOrder,
        Dictionary<PeerEndpoint, DateTimeOffset> nextConnectAttempt, IReadOnlyList<PeerEndpoint> candidates)
    {
        const int maxKnownSeedPeers = 512;
        for (int i = 0; i < candidates.Count; i++)
        {
            PeerEndpoint peer = candidates[i];
            if (knownPeers.Contains(peer))
            {
                continue;
            }

            if (knownPeers.Count == maxKnownSeedPeers)
            {
                PeerEndpoint oldest = peerOrder.Dequeue();
                knownPeers.Remove(oldest);
                nextConnectAttempt.Remove(oldest);
            }

            knownPeers.Add(peer);
            peerOrder.Enqueue(peer);
        }
    }

    private static void ConnectSeedPeers(PeerSeedServer seedServer, HashSet<PeerEndpoint> knownPeers,
        Dictionary<PeerEndpoint, DateTimeOffset> nextConnectAttempt)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (PeerEndpoint peer in knownPeers)
        {
            if (nextConnectAttempt.TryGetValue(peer, out DateTimeOffset due) && due > now)
            {
                continue;
            }

            if (seedServer.TryConnect(peer))
            {
                nextConnectAttempt[peer] = now + TimeSpan.FromMinutes(2);
            }
        }
    }

    private async Task AnnounceDhtAsync(DhtClient dhtClient, TorrentMetadata torrent, int announcePort,
        bool isSeed, CancellationToken token)
    {
        try
        {
            int announced = await dhtClient.AnnounceAsync(torrent.InfoHash, announcePort, isSeed, token);
            _log($"dht announced to {announced} nodes");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _log($"dht announce failed: {exception.Message}");
        }
    }

    private static TimeSpan ClampTrackerInterval(TimeSpan interval)
    {
        if (interval < TimeSpan.FromSeconds(30))
        {
            return TimeSpan.FromSeconds(30);
        }

        if (interval > TimeSpan.FromHours(1))
        {
            return TimeSpan.FromHours(1);
        }

        return interval;
    }

    private void StartPeerWorkers(
        TorrentMetadata torrent,
        PeerWireClient peerWire,
        HashSet<PeerEndpoint> knownPeers,
        HashSet<PeerEndpoint> recentlyFailed,
        Dictionary<Task, PeerEndpoint> activePeers,
        CancellationToken token)
    {
        if (activePeers.Count >= _options.MaxPeers)
        {
            return;
        }

        foreach (PeerEndpoint peer in knownPeers)
        {
            if (activePeers.Count >= _options.MaxPeers)
            {
                return;
            }

            if (recentlyFailed.Contains(peer) || IsActive(activePeers, peer))
            {
                continue;
            }

            Task task = Task.Run(() => peerWire.RunPeerAsync(peer, token), token);
            activePeers[task] = peer;
            _log($"peer {peer} connected slot for {torrent.Name}");
        }
    }

    private static bool IsActive(Dictionary<Task, PeerEndpoint> activePeers, PeerEndpoint peer)
    {
        foreach (PeerEndpoint activePeer in activePeers.Values)
        {
            if (activePeer.Equals(peer))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddKnownPeers(HashSet<PeerEndpoint> knownPeers, IReadOnlyList<PeerEndpoint> peers)
    {
        for (int i = 0; i < peers.Count; i++)
        {
            knownPeers.Add(peers[i]);
        }
    }

    private static TorrentClientOptions ValidateOptions(TorrentClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.TorrentPath, nameof(TorrentClientOptions.TorrentPath));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputDirectory, nameof(TorrentClientOptions.OutputDirectory));

        if (options.ListenPort < 0 || options.ListenPort > ushort.MaxValue ||
            (options.ListenPort == 0 && !options.SeedAfterCompletion))
        {
            throw new ArgumentOutOfRangeException(nameof(TorrentClientOptions.ListenPort), options.ListenPort, "Listen port must be in the range 1..65535, or zero when seeding is enabled.");
        }

        if (options.MaxPeers <= 0 || options.MaxPeers > MaxPeerConnections)
        {
            throw new ArgumentOutOfRangeException(nameof(TorrentClientOptions.MaxPeers), options.MaxPeers, $"Max peers must be in the range 1..{MaxPeerConnections}.");
        }

        if (options.MaxUploadPeers <= 0 || options.MaxUploadPeers > MaxUploadConnections)
        {
            throw new ArgumentOutOfRangeException(nameof(TorrentClientOptions.MaxUploadPeers), options.MaxUploadPeers,
                $"Max upload peers must be in the range 1..{MaxUploadConnections}.");
        }

        if (options.ExplicitPeers is null || options.ExplicitPeers.Count > 64)
        {
            throw new ArgumentException("Explicit peers must contain no more than 64 endpoints.", nameof(TorrentClientOptions.ExplicitPeers));
        }

        for (int i = 0; i < options.ExplicitPeers.Count; i++)
        {
            if (!MagnetLink.TryParsePeer(options.ExplicitPeers[i], out _))
            {
                throw new ArgumentException("Explicit peer endpoint is invalid.", nameof(TorrentClientOptions.ExplicitPeers));
            }
        }

        if (!options.EnableDht && !options.EnableTrackers && options.ExplicitPeers.Count == 0 && !options.SeedAfterCompletion)
        {
            throw new ArgumentException("A tracker, DHT, explicit peer, or enabled seeding is required.");
        }

        ValidatePositiveTimeout(options.TrackerTimeout, nameof(TorrentClientOptions.TrackerTimeout));
        ValidatePositiveTimeout(options.DhtLookupTimeout, nameof(TorrentClientOptions.DhtLookupTimeout));
        ValidatePositiveTimeout(options.DhtLookupInterval, nameof(TorrentClientOptions.DhtLookupInterval));
        ValidatePositiveTimeout(options.PeerTimeout, nameof(TorrentClientOptions.PeerTimeout));

        return options;
    }

    private static void ValidatePositiveTimeout(TimeSpan timeout, string name)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(name, timeout, "Timeout must be greater than zero and no more than one hour.");
        }
    }

    private async Task VerifyExistingPiecesAsync(
        TorrentMetadata torrent,
        TorrentStorage storage,
        PiecePicker picker,
        CancellationToken token)
    {
        _log("verifying existing payload data");
        byte[] buffer = GC.AllocateUninitializedArray<byte>(torrent.PieceLength);
        for (int i = 0; i < torrent.PieceCount; i++)
        {
            if (await storage.VerifyPieceAsync(i, buffer, token))
            {
                picker.MarkComplete(i);
            }

            if (i % 256 == 0 || i == torrent.PieceCount - 1)
            {
                _log($"verified {i + 1}/{torrent.PieceCount}; complete {picker.CompletedPieces}/{torrent.PieceCount}");
                ReportProgress(TorrentSessionPhase.Verifying, torrent, picker, $"Verified {i + 1}/{torrent.PieceCount}");
            }
        }
    }

    private void ReportProgress(TorrentSessionPhase phase, TorrentMetadata? torrent, PiecePicker? picker, string message)
    {
        TorrentSessionProgress snapshot = new(
            phase,
            torrent?.Name ?? string.Empty,
            torrent?.InfoHashHex ?? string.Empty,
            torrent?.TotalLength ?? 0,
            picker?.DownloadedBytes ?? 0,
            torrent?.PieceCount ?? 0,
            picker?.CompletedPieces ?? 0,
            Volatile.Read(ref _activePeerCount),
            Volatile.Read(ref _knownPeerCount),
            message,
            DateTimeOffset.UtcNow);
        Volatile.Write(ref _latestProgress, snapshot);
        _progress?.Report(snapshot);
    }

    private static byte[] CreatePeerId()
    {
        byte[] peerId = new byte[TorrentMetadata.Sha1Length];
        byte[] prefix = Encoding.ASCII.GetBytes("-NT0001-");
        prefix.CopyTo(peerId, 0);
        RandomNumberGenerator.Fill(peerId.AsSpan(prefix.Length));
        return peerId;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
