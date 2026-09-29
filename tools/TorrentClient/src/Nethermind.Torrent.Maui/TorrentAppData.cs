// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Torrent.Maui;

internal static class TorrentAppData
{
    public static string DirectoryPath
    {
        get
        {
            if (!OperatingSystem.IsLinux())
            {
                return FileSystem.AppDataDirectory;
            }

            string? dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrWhiteSpace(dataHome) || !Path.IsPathFullyQualified(dataHome))
            {
                dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            }

            return Path.Combine(dataHome, "nethermind-torrent");
        }
    }
}
