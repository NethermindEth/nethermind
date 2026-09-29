// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Torrent;

/// <summary>
/// Reports how much existing payload data has been checked against torrent piece hashes.
/// </summary>
/// <param name="ScannedPieces">Number of pieces checked so far.</param>
/// <param name="TotalPieces">Total number of pieces in the torrent.</param>
/// <param name="VerifiedPieces">Number of pieces whose hashes match.</param>
/// <param name="VerifiedBytes">Payload bytes covered by matching pieces.</param>
public readonly record struct TorrentVerificationProgress(int ScannedPieces, int TotalPieces, int VerifiedPieces, long VerifiedBytes);

/// <summary>
/// Recovers torrent progress from existing files without modifying them or contacting peers.
/// </summary>
public static class TorrentDataVerifier
{
    /// <summary>
    /// Checks existing payload files and returns the number of verified pieces and bytes.
    /// </summary>
    /// <param name="metadata">Metadata containing the expected piece hashes and file layout.</param>
    /// <param name="outputDirectory">Root directory containing the payload files.</param>
    /// <param name="progress">Optional progress receiver for periodic scan updates.</param>
    /// <param name="token">Token used to cancel the scan.</param>
    /// <returns>The final verified progress.</returns>
    public static async Task<TorrentVerificationProgress> VerifyAsync(
        TorrentMetadata metadata,
        string outputDirectory,
        IProgress<TorrentVerificationProgress>? progress = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        await using TorrentStorage storage = new(metadata, outputDirectory, readOnly: true);
        await storage.InitializeAsync(token);
        byte[] buffer = GC.AllocateUninitializedArray<byte>(metadata.PieceLength);
        int verifiedPieces = 0;
        long verifiedBytes = 0;
        for (int i = 0; i < metadata.PieceCount; i++)
        {
            token.ThrowIfCancellationRequested();
            if (await storage.VerifyPieceAsync(i, buffer, token))
            {
                verifiedPieces++;
                verifiedBytes += metadata.GetPieceSize(i);
            }

            if (i % 32 == 31 || i == metadata.PieceCount - 1)
            {
                progress?.Report(new TorrentVerificationProgress(i + 1, metadata.PieceCount, verifiedPieces, verifiedBytes));
            }
        }

        return new TorrentVerificationProgress(metadata.PieceCount, metadata.PieceCount, verifiedPieces, verifiedBytes);
    }
}
