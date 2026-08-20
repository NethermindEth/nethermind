// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Init.Snapshot;

internal static class SnapshotDatabase
{
    private const string MountArtifact = "lost+found";

    public static bool Exists(string dbPath)
    {
        if (File.Exists(dbPath))
            return true;
        if (!Directory.Exists(dbPath))
            return false;

        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(dbPath))
            {
                if (Path.GetFileName(entry) != MountArtifact)
                    return true;
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return true;
        }

        return false;
    }

    public static void Delete(string dbPath)
    {
        if (!Directory.Exists(dbPath))
            return;

        foreach (string entry in Directory.EnumerateFileSystemEntries(dbPath))
        {
            if (Path.GetFileName(entry) == MountArtifact)
                continue;

            if (Directory.Exists(entry))
                Directory.Delete(entry, true);
            else
                File.Delete(entry);
        }

        try
        {
            Directory.Delete(dbPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
