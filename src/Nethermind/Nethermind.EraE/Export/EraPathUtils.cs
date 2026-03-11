// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Testably.Abstractions;

namespace Nethermind.EraE;

public static class EraPathUtils
{
    private static readonly char[] _separator = ['-'];

    public const string FileExtension = ".erae";

    public static IEnumerable<string> GetAllEraFiles(string directoryPath, string network, IFileSystem fileSystem)
    {
        string[] entries = fileSystem.Directory.GetFiles(directoryPath, $"*{FileExtension}", new EnumerationOptions
        {
            RecurseSubdirectories = false,
            MatchCasing = MatchCasing.PlatformDefault
        });

        if (entries.Length == 0)
            yield break;

        foreach (string file in entries)
        {
            // Format: <network>-<epoch>-<hexroot>.erae
            string[] parts = Path.GetFileNameWithoutExtension(file).Split(_separator);
            if (parts.Length != 3 || !network.Equals(parts[0], StringComparison.OrdinalIgnoreCase)) continue;
            if (!uint.TryParse(parts[1], out _))
                throw new EraException($"Invalid erae filename: {Path.GetFileName(file)}");

            yield return file;
        }
    }

    public static IEnumerable<string> GetAllEraFiles(string directoryPath, string network)
        => GetAllEraFiles(directoryPath, network, new RealFileSystem());

    public static string Filename(string network, long epoch, Hash256 accumulatorRoot)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(network);
        ArgumentNullException.ThrowIfNull(accumulatorRoot);
        ArgumentOutOfRangeException.ThrowIfLessThan(epoch, 0);

        return $"{network}-{epoch:D5}-{accumulatorRoot.ToString(true)[2..10]}{FileExtension}";
    }

    public static ValueHash256 ExtractHashFromChecksumEntry(string s)
    {
        ValueHash256 result = default;
        ReadOnlySpan<char> span = s.AsSpan();
        int idx = span.IndexOf(' ');
        ReadOnlySpan<char> token = idx == -1 ? span : span[..idx];
        Bytes.FromHexString(token, result.BytesAsSpan);
        return result;
    }
}
