// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Nethermind.Torrent;

/// <summary>Configures peer discovery and metadata transfer for a v1 magnet link.</summary>
public sealed class MagnetResolveOptions
{
    /// <summary>Gets or sets whether HTTP and UDP trackers are queried. Defaults to true.</summary>
    public bool EnableTrackers { get; init; } = true;

    /// <summary>Gets or sets whether DHT is queried. Defaults to true.</summary>
    public bool EnableDht { get; init; } = true;

    /// <summary>Gets or sets the TCP port announced to trackers, from 1 to 65535. Defaults to 6881.</summary>
    public int ListenPort { get; init; } = 6881;

    /// <summary>Gets or sets the total tracker discovery timeout. Defaults to 20 seconds.</summary>
    public TimeSpan TrackerTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Gets or sets the DHT discovery timeout. Defaults to 15 seconds.</summary>
    public TimeSpan DhtTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Gets or sets the timeout for one peer's connection and metadata transfer. Defaults to 30 seconds.</summary>
    public TimeSpan PeerTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Resolves BEP 9 metadata and constructs a v1 torrent file from a magnet URI.</summary>
public static class MagnetMetadataResolver
{
    private const int MaxPeerAttempts = 128;
    private const int MaxUnrelatedMessages = 64;
    private const int MaxAttemptsBeforeDht = 80;
    private static readonly TimeSpan MaxResolutionTime = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan FallbackPhaseTime = TimeSpan.FromSeconds(45);

    /// <summary>Retrieves the raw info dictionary from a peer and returns a complete .torrent file.</summary>
    /// <remarks>Resolution has a three-minute overall deadline and accepts at most 8 MiB of metadata.</remarks>
    /// <param name="magnetUri">A v1 btih magnet URI.</param>
    /// <param name="options">Peer discovery and timeout settings.</param>
    /// <param name="log">Optional diagnostic callback.</param>
    /// <param name="token">Cancels discovery and peer transfer.</param>
    /// <returns>Bencoded .torrent file bytes with the original, hash-verified info dictionary.</returns>
    /// <exception cref="FormatException">The magnet URI is invalid.</exception>
    /// <exception cref="InvalidOperationException">No peer supplied valid metadata.</exception>
    public static Task<byte[]> ResolveAsync(string magnetUri, MagnetResolveOptions options, Action<string>? log, CancellationToken token)
        => ResolveAsync(magnetUri, options, log, token, null);

    internal static async Task<byte[]> ResolveAsync(string magnetUri, MagnetResolveOptions options, Action<string>? log,
        CancellationToken token, Func<byte[], CancellationToken, Task<IReadOnlyList<PeerEndpoint>>>? dhtLookup,
        Func<PeerEndpoint, byte[], byte[], CancellationToken, Task<byte[]>>? downloadInfo = null)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        MagnetLink link = MagnetLink.Parse(magnetUri);
        token.ThrowIfCancellationRequested();

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(MaxResolutionTime);
        CancellationToken workToken = deadline.Token;
        Action<string> writeLog = log ?? (_ => { });
        byte[] peerId = new byte[20];
        "-NT0001-"u8.CopyTo(peerId);
        RandomNumberGenerator.Fill(peerId.AsSpan(8));
        HashSet<PeerEndpoint> tried = [];
        List<string> peerFailures = [];
        int attempts = 0;

        byte[]? torrent;
        try
        {
            using CancellationTokenSource directDeadline = CancellationTokenSource.CreateLinkedTokenSource(workToken);
            if (options.EnableDht || options.EnableTrackers && link.Trackers.Count > 0)
            {
                directDeadline.CancelAfter(FallbackPhaseTime);
            }

            torrent = await TryPeersAsync(link.Peers, link, peerId, options, tried, peerFailures, writeLog,
                directDeadline.Token, attempts, options.EnableDht ? MaxAttemptsBeforeDht : MaxPeerAttempts, downloadInfo);
        }
        catch (OperationCanceledException) when (!workToken.IsCancellationRequested)
        {
            writeLog("magnet explicit peers timed out; trying discovery fallback");
            torrent = null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("Magnet metadata resolution exceeded three minutes.");
        }
        attempts = tried.Count;
        if (torrent is not null)
        {
            return torrent;
        }

        IReadOnlyList<PeerEndpoint> trackerPeers = [];
        if (options.EnableTrackers && link.Trackers.Count > 0)
        {
            TimeSpan trackerTimeout = options.EnableDht && options.TrackerTimeout > FallbackPhaseTime
                ? FallbackPhaseTime : options.TrackerTimeout;
            using HttpClient httpClient = new() { Timeout = trackerTimeout };
            TrackerClient tracker = new(httpClient, writeLog, trackerTimeout);
            string trackerKey = RandomNumberGenerator.GetHexString(8);
            try
            {
                using CancellationTokenSource trackerDeadline = CancellationTokenSource.CreateLinkedTokenSource(workToken);
                trackerDeadline.CancelAfter(trackerTimeout);
                TrackerAnnounceResult result = await tracker.AnnounceAsync(
                    link.Trackers, link.InfoHash, 1, peerId, trackerKey,
                    options.ListenPort, 0, 0, trackerDeadline.Token, stopOnPeers: false);
                trackerPeers = result.Peers;
                workToken.ThrowIfCancellationRequested();
                writeLog($"magnet tracker peers: {result.Peers.Count}");
                using CancellationTokenSource peerDeadline = CancellationTokenSource.CreateLinkedTokenSource(workToken);
                if (options.EnableDht)
                {
                    peerDeadline.CancelAfter(FallbackPhaseTime);
                }

                torrent = await TryPeersAsync(result.Peers, link, peerId, options, tried, peerFailures, writeLog,
                    peerDeadline.Token, attempts, options.EnableDht ? MaxAttemptsBeforeDht : MaxPeerAttempts, downloadInfo);
                attempts = tried.Count;
                if (torrent is not null)
                {
                    return torrent;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                throw new TimeoutException("Magnet metadata resolution exceeded three minutes.");
            }
            catch (OperationCanceledException)
            {
                writeLog("magnet tracker peers timed out; trying DHT fallback");
            }
            catch (Exception exception)
            {
                writeLog($"magnet tracker discovery failed: {exception.Message}");
            }
            finally
            {
                using CancellationTokenSource stopDeadline = new(TimeSpan.FromSeconds(3));
                try
                {
                    await tracker.AnnounceEventAsync(link.InfoHash, 1, peerId, trackerKey,
                        options.ListenPort, 0, 0, "stopped", stopDeadline.Token, concurrent: true);
                }
                catch (OperationCanceledException)
                {
                    writeLog("magnet tracker stopped announce timed out");
                }
            }
        }

        attempts = tried.Count;

        if (options.EnableDht && attempts < MaxPeerAttempts)
        {
            try
            {
                using CancellationTokenSource dhtDeadline = CancellationTokenSource.CreateLinkedTokenSource(workToken);
                dhtDeadline.CancelAfter(options.DhtTimeout);
                IReadOnlyList<PeerEndpoint> peers;
                if (dhtLookup is null)
                {
                    await using DhtClient dht = new(peerId, writeLog);
                    peers = await dht.FindPeersAsync(link.InfoHash, dhtDeadline.Token);
                }
                else
                {
                    peers = await dhtLookup(link.InfoHash, dhtDeadline.Token);
                }

                torrent = await TryPeersAsync(peers, link, peerId, options, tried, peerFailures, writeLog,
                    workToken, attempts, MaxPeerAttempts, downloadInfo);
                if (torrent is not null)
                {
                    return torrent;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                throw new TimeoutException("Magnet metadata resolution exceeded three minutes.");
            }
            catch (Exception exception)
            {
                writeLog($"magnet DHT discovery failed: {exception.Message}");
            }
        }

        if (options.EnableDht && tried.Count < MaxPeerAttempts)
        {
            try
            {
                torrent = await TryPeersAsync(link.Peers, link, peerId, options, tried, peerFailures, writeLog,
                    workToken, tried.Count, MaxPeerAttempts, downloadInfo);
                if (torrent is not null)
                {
                    return torrent;
                }

                torrent = await TryPeersAsync(trackerPeers, link, peerId, options, tried, peerFailures, writeLog,
                    workToken, tried.Count, MaxPeerAttempts, downloadInfo);
                if (torrent is not null)
                {
                    return torrent;
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                throw new TimeoutException("Magnet metadata resolution exceeded three minutes.");
            }
        }

        token.ThrowIfCancellationRequested();
        if (deadline.IsCancellationRequested)
        {
            throw new TimeoutException("Magnet metadata resolution exceeded three minutes.");
        }

        string detail = peerFailures.Count == 0 ? "No peers were available." : $"Last peer error: {peerFailures[^1]}";
        throw new InvalidOperationException($"No magnet peer supplied valid BEP 9 metadata for the requested infohash. {detail}");
    }

    private static async Task<byte[]?> TryPeersAsync(
        IReadOnlyList<PeerEndpoint> peers, MagnetLink link, byte[] peerId, MagnetResolveOptions options,
        HashSet<PeerEndpoint> tried, List<string> failures, Action<string> log, CancellationToken token, int attempts,
        int attemptLimit, Func<PeerEndpoint, byte[], byte[], CancellationToken, Task<byte[]>>? downloadInfo)
    {
        for (int i = 0; i < peers.Count && attempts < attemptLimit; i++)
        {
            PeerEndpoint peer = peers[i];
            if (!tried.Add(peer))
            {
                continue;
            }

            attempts++;
            try
            {
                using CancellationTokenSource peerDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                peerDeadline.CancelAfter(options.PeerTimeout);
                byte[] info = downloadInfo is null
                    ? await DownloadInfoAsync(peer, link.InfoHash, peerId, peerDeadline.Token)
                    : await downloadInfo(peer, link.InfoHash, peerId, peerDeadline.Token);
                byte[] torrent = MagnetMetadataProtocol.CreateTorrent(info, link.InfoHash, link.Trackers);
                peerDeadline.Token.ThrowIfCancellationRequested();
                log($"magnet metadata resolved from {peer}");
                return torrent;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                failures.Add($"{peer} timed out");
                log($"magnet peer {peer} timed out");
            }
            catch (Exception exception)
            {
                failures.Add($"{peer}: {exception.Message}");
                log($"magnet peer {peer} failed: {exception.Message}");
            }
        }

        return null;
    }

    private static async Task<byte[]> DownloadInfoAsync(PeerEndpoint peer, byte[] infoHash, byte[] peerId, CancellationToken token)
    {
        using TcpClient client = new();
        client.NoDelay = true;
        await client.ConnectAsync(peer.Host, peer.Port, token);
        await using NetworkStream stream = client.GetStream();
        await stream.WriteAsync(MagnetMetadataProtocol.CreateHandshake(infoHash, peerId), token);
        byte[] handshake = new byte[68];
        await stream.ReadExactlyAsync(handshake, token);
        MagnetMetadataProtocol.ValidateHandshake(handshake, infoHash);
        await stream.WriteAsync(MagnetMetadataProtocol.CreateExtendedHandshake(), token);

        PeerMessage extension = await ReadExtendedAsync(stream, 0, token);
        (byte remoteId, int metadataSize) = MagnetMetadataProtocol.ParseExtendedHandshake(extension.Payload.Span);
        byte[] info = new byte[metadataSize];
        int pieceCount = (metadataSize + MagnetMetadataProtocol.PieceSize - 1) / MagnetMetadataProtocol.PieceSize;
        for (int piece = 0; piece < pieceCount; piece++)
        {
            await stream.WriteAsync(MagnetMetadataProtocol.CreateRequest(remoteId, piece), token);
            byte[]? block = null;
            for (int ignored = 0; block is null && ignored < MaxUnrelatedMessages; ignored++)
            {
                PeerMessage response = await ReadExtendedAsync(stream, MagnetMetadataProtocol.LocalMetadataId, token);
                block = MagnetMetadataProtocol.ParsePiece(response.Payload.Span, piece, metadataSize);
            }

            if (block is null)
            {
                throw new InvalidDataException("Peer sent too many unknown metadata messages.");
            }

            block.CopyTo(info.AsSpan(piece * MagnetMetadataProtocol.PieceSize));
        }

        return info;
    }

    private static async Task<PeerMessage> ReadExtendedAsync(NetworkStream stream, byte extensionId, CancellationToken token)
    {
        for (int i = 0; i < MaxUnrelatedMessages; i++)
        {
            byte[] lengthBytes = new byte[4];
            await stream.ReadExactlyAsync(lengthBytes, token);
            int length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
            if (length == 0)
            {
                continue;
            }

            if (length < 1 || length > MagnetMetadataProtocol.MaxMessageSize)
            {
                throw new InvalidDataException($"Peer message length {length} is out of bounds.");
            }

            byte[] message = new byte[length];
            await stream.ReadExactlyAsync(message, token);
            if (message[0] == (byte)PeerMessageId.Extended && length >= 2 && message[1] == extensionId)
            {
                return new PeerMessage(PeerMessageId.Extended, message.AsMemory(2));
            }
        }

        throw new InvalidDataException("Peer sent too many unrelated messages during metadata transfer.");
    }

    private static void ValidateOptions(MagnetResolveOptions options)
    {
        if (options.ListenPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(options.ListenPort), "Listen port must be in the range 1..65535.");
        }

        ValidateTimeout(options.TrackerTimeout, nameof(options.TrackerTimeout));
        ValidateTimeout(options.DhtTimeout, nameof(options.DhtTimeout));
        ValidateTimeout(options.PeerTimeout, nameof(options.PeerTimeout));
    }


    private static void ValidateTimeout(TimeSpan timeout, string name)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(3))
        {
            throw new ArgumentOutOfRangeException(name, "Timeout must be greater than zero and no more than three minutes.");
        }
    }
}
