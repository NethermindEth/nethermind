// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.EraE;

public static class EraPathUtils
{
    private static readonly char[] separator = new char[] { '-' };

    public static IEnumerable<string> GetAllEraFiles(string directoryPath, string network, IFileSystem fileSystem)
    {
        var entries = fileSystem.Directory.GetFiles(directoryPath, "*.erae", new EnumerationOptions()
        {
            RecurseSubdirectories = false,
            MatchCasing = MatchCasing.PlatformDefault
        });

        if (entries.Length == 0)
            yield break;

        uint next = 0;
        foreach (string file in entries)
        {
            // Format: <network>-<epoch>.erae
            string[] parts = Path.GetFileNameWithoutExtension(file).Split(separator);
            if (!network.Equals(parts[0], StringComparison.OrdinalIgnoreCase)) continue;
            if (!uint.TryParse(parts[1], out uint epoch))
                throw new Era1.EraException($"Invalid era1 filename: {Path.GetFileName(file)}");

            next++;
            yield return file;
        }
    }

    public static IEnumerable<string> GetAllEraFiles(string directoryPath, string network)
    {
        return GetAllEraFiles(directoryPath, network, new FileSystem());
    }

    public static string Filename(string network, long epoch, Hash256 root)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(network);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentOutOfRangeException.ThrowIfLessThan(epoch, 0);

        return $"{network}-{epoch:D5}.erae";
    }

    public static ValueHash256 ExtractHashFromAccumulatorAndCheckSumEntry(string s)
    {
        ValueHash256 result = default;
        ReadOnlySpan<char> span = s.AsSpan();
        int idx = span.IndexOf(' ');
        ReadOnlySpan<char> token = idx == -1 ? span : span[..idx];
        Bytes.FromHexString(token, result.BytesAsSpan);
        return result;
    }
}
