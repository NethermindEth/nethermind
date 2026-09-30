// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Torrent;

/// <summary>
/// Describes the current high-level stage of a torrent session.
/// </summary>
public enum TorrentSessionPhase
{
    /// <summary>
    /// The torrent metadata is being read and decoded.
    /// </summary>
    LoadingMetadata,

    /// <summary>
    /// Payload files and directories are being prepared.
    /// </summary>
    InitializingStorage,

    /// <summary>
    /// Existing payload data is being verified against piece hashes.
    /// </summary>
    Verifying,

    /// <summary>
    /// Trackers or DHT are being queried for peers.
    /// </summary>
    DiscoveringPeers,

    /// <summary>
    /// The client is connected to peers and downloading pieces.
    /// </summary>
    Downloading,

    /// <summary>
    /// Every piece has been verified.
    /// </summary>
    Completed,

    /// <summary>
    /// Verified pieces are available to other peers.
    /// </summary>
    Seeding,
}

/// <summary>
/// Immutable progress snapshot emitted by a running torrent session.
/// </summary>
/// <param name="Phase">Current high-level session stage.</param>
/// <param name="TorrentName">Torrent display name, when metadata has been loaded.</param>
/// <param name="InfoHashHex">Lowercase hexadecimal info-hash, when metadata has been loaded.</param>
/// <param name="TotalBytes">Total payload length in bytes.</param>
/// <param name="DownloadedBytes">Verified payload bytes.</param>
/// <param name="PieceCount">Total number of pieces.</param>
/// <param name="CompletedPieces">Number of verified pieces.</param>
/// <param name="ActivePeers">Current active peer workers.</param>
/// <param name="KnownPeers">Known peer endpoints from trackers and DHT.</param>
/// <param name="Message">Short status message associated with this snapshot.</param>
/// <param name="Timestamp">UTC timestamp when the snapshot was emitted.</param>
public sealed record TorrentSessionProgress(
    TorrentSessionPhase Phase,
    string TorrentName,
    string InfoHashHex,
    long TotalBytes,
    long DownloadedBytes,
    int PieceCount,
    int CompletedPieces,
    int ActivePeers,
    int KnownPeers,
    string Message,
    DateTimeOffset Timestamp);

/// <summary>Network transfer observed during one active download run.</summary>
/// <param name="PayloadBytesReceived">Accepted peer-wire piece payload bytes, including pieces that later fail verification.</param>
/// <param name="VerifiedBytesFromPeers">Accepted peer payload bytes that passed piece SHA-1 verification.</param>
/// <param name="ActiveTime">Elapsed discovery and download time, excluding verification and time between runs.</param>
/// <param name="ContributingPeers">Distinct handshake peer IDs that supplied at least one verified piece in this run.</param>
/// <param name="UploadedBytes">Piece payload bytes written to requesting peers during this run.</param>
/// <param name="ActiveUploadPeers">Current inbound peer connections serving this torrent.</param>
public readonly record struct TorrentTransferSnapshot(long PayloadBytesReceived, long VerifiedBytesFromPeers,
    TimeSpan ActiveTime, int ContributingPeers, long UploadedBytes = 0, int ActiveUploadPeers = 0);
