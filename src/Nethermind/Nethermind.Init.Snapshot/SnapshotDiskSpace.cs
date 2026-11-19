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
        // Only the database directory's drive, which GetDriveInfos lists first, must hold the whole snapshot.
        // A linked disk holds only part of the database; the free-space checks still monitor it.
        if (drives is [IDriveInfo drive, ..] && drive.AvailableFreeSpace < required)
            throw new IOException(
                $"Insufficient disk space on '{drive.RootDirectory.FullName}' to {operation} the snapshot: " +
                $"need at least {required} bytes, {drive.AvailableFreeSpace} available.");
    }
}
