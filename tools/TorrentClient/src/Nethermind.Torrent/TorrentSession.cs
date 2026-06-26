// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    /// Gets or sets the TCP port announced to trackers. The current CLI is download-only and does not listen yet.
    /// </summary>
    public int ListenPort { get; init; } = 6881;

    /// <summary>
    /// Gets or sets the maximum number of concurrent peer connections.
    /// </summary>
    public int MaxPeers { get; init; } = 32;

    /// <summary>
    /// Gets or sets whether the DHT fallback should run in addition to tracker announces.
    /// </summary>
    public bool EnableDht { get; init; } = true;

    /// <summary>
    /// Gets or sets whether HTTP and UDP trackers should be queried.
    /// </summary>
    public bool EnableTrackers { get; init; } = true;

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

    private readonly TorrentClientOptions _options = ValidateOptions(options);
    private readonly Action<string> _log = log ?? Console.WriteLine;
    private readonly IProgress<TorrentSessionProgress>? _progress = progress;
    private readonly byte[] _peerId = CreatePeerId();
    private readonly string _trackerKey = RandomNumberGenerator.GetHexString(8, lowercase: true);
    private int _activePeerCount;
    private int _knownPeerCount;

    /// <summary>
    /// Loads metadata, connects to the swarm, and downloads until all pieces are verified.
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

        using HttpClient httpClient = new()
        {
            Timeout = _options.TrackerTimeout,
        };

        TrackerClient trackerClient = new(httpClient, message => _log(message), _options.TrackerTimeout);
        DhtClient? dhtClient = null;
        if (_options.EnableDht)
        {
            dhtClient = new DhtClient(_peerId, message => _log(message));
        }

        bool completed = false;
        try
        {
            completed = await DownloadAsync(torrent, storage, picker, trackerClient, dhtClient, token);
            if (completed)
            {
                if (_options.EnableTrackers)
                {
                    await trackerClient.AnnounceEventAsync(
                        torrent,
                        _peerId,
                        _trackerKey,
                        _options.ListenPort,
                        picker.DownloadedBytes,
                        uploaded: 0,
                        "completed",
                        token);
                }

                ReportProgress(TorrentSessionPhase.Completed, torrent, picker, "Torrent complete");
            }
        }
        finally
        {
            using CancellationTokenSource stoppedCts = new(_options.TrackerTimeout);
            if (_options.EnableTrackers)
            {
                try
                {
                    await trackerClient.AnnounceEventAsync(
                        torrent,
                        _peerId,
                        _trackerKey,
                        _options.ListenPort,
                        picker.DownloadedBytes,
                        uploaded: 0,
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
        CancellationToken token)
    {
        Dictionary<Task, PeerEndpoint> activePeers = [];
        HashSet<PeerEndpoint> recentlyFailed = [];
        HashSet<PeerEndpoint> knownPeers = [];
        DateTimeOffset nextAnnounce = DateTimeOffset.MinValue;
        DateTimeOffset lastDht = DateTimeOffset.MinValue;
        PeerWireClient peerWire = new(
            torrent,
            _peerId,
            picker,
            storage,
            _log,
            _options.PeerTimeout,
            (pieceIndex, peer) => ReportProgress(
                TorrentSessionPhase.Downloading,
                torrent,
                picker,
                $"Piece {pieceIndex + 1}/{torrent.PieceCount} from {peer}"));
        using CancellationTokenSource peerCts = CancellationTokenSource.CreateLinkedTokenSource(token);

        try
        {
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
                        _options.ListenPort,
                        picker.DownloadedBytes,
                        uploaded: 0,
                        token);
                    AddKnownPeers(knownPeers, trackerResult.Peers);
                    Volatile.Write(ref _knownPeerCount, knownPeers.Count);
                    nextAnnounce = DateTimeOffset.UtcNow + ClampTrackerInterval(trackerResult.Interval);
                    _log($"tracker peers: {knownPeers.Count}");
                    ReportProgress(TorrentSessionPhase.DiscoveringPeers, torrent, picker, $"Tracker peers: {knownPeers.Count}");
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

        if (options.ListenPort <= 0 || options.ListenPort > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(TorrentClientOptions.ListenPort), options.ListenPort, "Listen port must be in the range 1..65535.");
        }

        if (options.MaxPeers <= 0 || options.MaxPeers > MaxPeerConnections)
        {
            throw new ArgumentOutOfRangeException(nameof(TorrentClientOptions.MaxPeers), options.MaxPeers, $"Max peers must be in the range 1..{MaxPeerConnections}.");
        }

        if (!options.EnableDht && !options.EnableTrackers)
        {
            throw new ArgumentException("At least one peer discovery method must be enabled.");
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

    private void ReportProgress(TorrentSessionPhase phase, TorrentMetadata? torrent, PiecePicker? picker, string message) =>
        _progress?.Report(new TorrentSessionProgress(
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
            DateTimeOffset.UtcNow));

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
