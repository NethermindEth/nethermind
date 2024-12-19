// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;

namespace Nethermind.Era1;

public static class EraPathUtils
{
    private static readonly char[] separator = new char[] { '-' };

    public static IEnumerable<string> GetAllEraFiles(string directoryPath, string network, IFileSystem fileSystem)
    {
        var entries = fileSystem.Directory.GetFiles(directoryPath, "*.era1", new EnumerationOptions()
        {
            RecurseSubdirectories = false,
            MatchCasing = MatchCasing.PlatformDefault
        });

        if (!entries.Any())
            yield break;

        uint next = 0;
        foreach (string file in entries)
        {
            // Format: <network>-<epoch>-<hexroot>.era1
            string[] parts = Path.GetFileName(file).Split(separator);
            if (parts.Length != 3 || !network.Equals(parts[0], StringComparison.OrdinalIgnoreCase)) continue;
            if (!uint.TryParse(parts[1], out uint epoch))
                throw new EraException($"Invalid era1 filename: {Path.GetFileName(file)}");

            next++;
            yield return file;
        }
    }

    public static IEnumerable<string> GetAllEraFiles(string directoryPath, string network)
    {
        return GetAllEraFiles(directoryPath, network, new FileSystem());
    }
}
