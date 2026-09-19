// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Pbt.Image;

/// <summary>Produces a verified portable bundle without publishing native PBT or migration state.</summary>
internal static class PbtOfflineExport
{
    public static void Export(PbtBootstrapLease lease, string outputPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (lease.Snapshot is not null || lease.Preimages is not null || lease.OfflineSource is null || lease.OfflineCode is null)
            throw new InvalidOperationException("Offline export requires a genesis or immutable preimage-flat source.");
        string output = Path.GetFullPath(outputPath);
        string parent = Path.GetDirectoryName(output) ?? throw new InvalidDataException("Export requires a parent directory.");
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Export destination already exists.");
        Directory.CreateDirectory(parent);
        string temporary = Path.Combine(parent, $".pbt-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        try
        {
            using (FileStream snapshot = new(Path.Combine(temporary, "snapshot.pbt"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (FileStream preimages = new(Path.Combine(temporary, "preimages.bin"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (FileStream manifest = new(Path.Combine(temporary, "manifest.json"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                if (!lease.IsAnchorCurrent()) throw new InvalidOperationException("Export anchor is no longer current.");
                PbtOfflineSource.WriteArtifacts(lease.OfflineSource, lease.OfflineCode, lease.Identity, lease.Anchor,
                    temporary, snapshot, preimages, manifest, cancellationToken: cancellationToken);
                snapshot.Position = 0;
                preimages.Position = 0;
                using PbtVerifiedImage verified = PbtImageVerifier.Verify(snapshot, preimages, lease.Identity, lease.Anchor, temporary, cancellationToken);
                snapshot.Flush(flushToDisk: true);
                preimages.Flush(flushToDisk: true);
                manifest.Flush(flushToDisk: true);
                cancellationToken.ThrowIfCancellationRequested();
                if (!lease.IsAnchorCurrent()) throw new InvalidOperationException("Export anchor changed during verification.");
            }
            // Same-parent rename publishes all three files together and refuses an existing destination.
            Directory.Move(temporary, output);
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }
}
