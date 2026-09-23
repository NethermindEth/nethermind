// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using FlatPersistence = Nethermind.State.Flat.Persistence.IPersistence;

namespace Nethermind.State.Pbt.Image;

/// <summary>Produces a verified EIP-8347 bundle without publishing native PBT or migration state.</summary>
/// <remarks>The source must be a preimage-flat reader pinned at the anchor for the whole call: the caller
/// stops the state advancing before exporting, and <paramref name="isAnchorCurrent"/> re-checks that the
/// anchor is still the one the chain agrees on, since verification takes long enough for a reorg to land.</remarks>
internal static class PbtOfflineExport
{
    /// <param name="sortBufferBytes">Sort budget per scan worker, split between the leaf and preimage spools.</param>
    /// <param name="workerCount">Scan workers; zero uses the processor count.</param>
    public static void Export(FlatPersistence.IPersistenceReader source, IReadOnlyKeyValueStore code, PbtImageAnchor anchor,
        string outputPath, string scratchDirectory, Func<bool> isAnchorCurrent, int sortBufferBytes, int workerCount,
        ILogManager logManager, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ILogger logger = logManager.GetClassLogger(typeof(PbtOfflineExport));
        Stopwatch exporting = Stopwatch.StartNew();
        string output = Path.GetFullPath(outputPath);
        string parent = Path.GetDirectoryName(output) ?? throw new InvalidDataException("Export requires a parent directory.");
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Export destination already exists.");
        Directory.CreateDirectory(parent);
        string temporary = Path.Combine(parent, $".pbt-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        if (logger.IsInfo) logger.Info($"Exporting the EIP-8347 anchor {anchor.Header.ToString(BlockHeader.Format.Short)} to {output}.");
        try
        {
            using (FileStream snapshot = new(Path.Combine(temporary, "snapshot.pbt"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (FileStream preimages = new(Path.Combine(temporary, "preimages.bin"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                if (!isAnchorCurrent()) throw new InvalidOperationException("Export anchor is no longer current.");
                PbtArtifactWriter.PbtArtifactDigests digests = PbtOfflineSource.WriteArtifacts(source, code, anchor,
                    temporary, snapshot, preimages, logManager, sortBufferBytes, workerCount, cancellationToken);
                snapshot.Position = 0;
                preimages.Position = 0;
                using PbtVerifiedImage verified = PbtImageVerifier.Verify(snapshot, preimages, anchor, temporary, logManager, cancellationToken);
                snapshot.Flush(flushToDisk: true);
                preimages.Flush(flushToDisk: true);
                cancellationToken.ThrowIfCancellationRequested();
                if (!isAnchorCurrent()) throw new InvalidOperationException("Export anchor changed during verification.");
                if (logger.IsInfo)
                    logger.Info($"EIP-8347 anchor {anchor.Header.Number} digests: snapshot {digests.Snapshot}, preimages {digests.Preimages}.");
            }
            // Same-parent rename publishes both files together and refuses an existing destination.
            Directory.Move(temporary, output);
            if (logger.IsInfo) logger.Info($"Exported the verified EIP-8347 artifacts to {output} in {exporting.Elapsed:hh\\:mm\\:ss}.");
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }
}
