// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using EraException = Nethermind.Era1.EraException;
using Testably.Abstractions;

namespace Nethermind.EraE.Export;

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
        ArgumentException.ThrowIfNullOrEmpty(network);
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

    public static (long Epoch, ValueHash256 Hash) ParseChecksumEntry(string line)
    {
        ValueHash256 hash = ExtractHashFromChecksumEntry(line);

        int nameStart = line.IndexOf(' ');
        if (nameStart == -1)
            throw new ArgumentException($"Invalid checksum entry (no filename): {line}");

        string filename = line[(nameStart + 1)..].TrimStart();
        string nameWithoutExt = Path.GetFileNameWithoutExtension(filename);
        string[] parts = nameWithoutExt.Split(_separator);
        if (parts.Length != 3 || !long.TryParse(parts[1], out long epoch))
            throw new ArgumentException($"Invalid checksum entry filename: {filename}");

        return (epoch, hash);
    }
}
