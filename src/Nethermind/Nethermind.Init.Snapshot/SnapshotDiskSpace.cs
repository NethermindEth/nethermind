// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;

namespace Nethermind.Init.Snapshot;

internal static class SnapshotDiskSpace
{
    private const double ExtractionSpaceMultiplier = 1.5;

    public static long GetRequiredSpaceForDownload(long totalSize, long existingSize) =>
        totalSize - existingSize + GetRequiredSpaceForExtraction(totalSize);

    public static long GetRequiredSpaceForExtraction(long snapshotSize) =>
        (long)(snapshotSize * ExtractionSpaceMultiplier);

    public static void Check(IDriveInfo[] drives, long required, string operation)
    {
        foreach (IDriveInfo drive in drives)
        {
            if (drive.AvailableFreeSpace < required)
                throw new IOException(
                    $"Insufficient disk space on '{drive.RootDirectory.FullName}' to {operation} the snapshot: " +
                    $"need at least {required} bytes, {drive.AvailableFreeSpace} available.");
        }
    }
}
