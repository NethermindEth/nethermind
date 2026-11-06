// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.IO.Abstractions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Testably.Abstractions;
using Nethermind.Era1.Exceptions;

namespace Nethermind.Era1;

public static class EraPathUtils
{
    private static readonly char[] separator = new char[] { '-' };

    public static IEnumerable<string> GetAllEraFiles(string directoryPath, string network, IFileSystem fileSystem)
    {
        string[] entries = fileSystem.Directory.GetFiles(directoryPath, "*.era1", new EnumerationOptions()
        {
            RecurseSubdirectories = false,
            MatchCasing = MatchCasing.PlatformDefault
        });

        if (entries.Length == 0)
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

    public static IEnumerable<string> GetAllEraFiles(string directoryPath, string network) => GetAllEraFiles(directoryPath, network, new RealFileSystem());

    public static string Filename(string network, ulong epoch, Hash256 root)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(network);
        ArgumentNullException.ThrowIfNull(root);

        return $"{network}-{epoch:D5}-{root.ToString(true)[2..10]}.era1";
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

    /// <summary>
    /// Parses the lines of an accumulator or checksum manifest into the hash of each epoch.
    /// </summary>
    /// <remarks>
    /// A line that names a file is keyed by the epoch in that name, which older exports also wrote correctly when the
    /// rest of the name was a placeholder. A line with only a hash, as geth writes checksums.txt, is keyed by its
    /// position: line i belongs to epoch <paramref name="firstEpoch"/> + i.
    /// </remarks>
    /// <param name="fileNames">When given, receives the file name each named line lists for its epoch.</param>
    /// <exception cref="EraException">A listed file name has no epoch.</exception>
    internal static Dictionary<long, ValueHash256> ParseManifest(string[] lines, long firstEpoch, Dictionary<long, string>? fileNames = null)
    {
        Dictionary<long, ValueHash256> entries = new(lines.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            int separatorIndex = line.IndexOf(' ');
            string fileName = separatorIndex == -1 ? "" : line[(separatorIndex + 1)..].Trim();

            long epoch = firstEpoch + i;
            if (fileName.Length > 0 && !TryGetEpoch(fileName, out epoch))
                throw new EraException($"Invalid manifest entry: {line}");
            if (fileName.Length > 0 && fileNames is not null)
                fileNames[epoch] = fileName;

            entries[epoch] = ExtractHashFromAccumulatorAndCheckSumEntry(line);
        }

        return entries;
    }

    internal static bool TryGetEpoch(string fileName, out long epoch)
    {
        string[] parts = Path.GetFileName(fileName).Split(separator);
        epoch = 0;
        return parts.Length == 3 && long.TryParse(parts[1], out epoch);
    }

    /// <summary>
    /// Recognizes an era1 file of <paramref name="network"/> that an export may replace:
    /// <c>&lt;network&gt;-&lt;epoch&gt;-&lt;8 hex digits&gt;.era1</c>. Stricter than <see cref="TryGetEpoch"/>, so it never
    /// matches a file that is not an era1 file.
    /// </summary>
    /// <param name="isPlaceholder">The root part is zero: the file an export writes before it knows the accumulator root.</param>
    internal static bool TryParseEpochFile(string path, string network, out ulong epoch, out bool isPlaceholder)
    {
        epoch = 0;
        isPlaceholder = false;
        if (!Path.GetExtension(path.AsSpan()).Equals(".era1", StringComparison.OrdinalIgnoreCase)) return false;

        string[] parts = Path.GetFileNameWithoutExtension(path).Split(separator);
        if (parts.Length != 3 || !network.Equals(parts[0], StringComparison.OrdinalIgnoreCase)
            || !ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out epoch)
            || parts[2].Length != 8 || !parts[2].All(char.IsAsciiHexDigit))
            return false;

        isPlaceholder = parts[2] == "00000000";
        return true;
    }
}
