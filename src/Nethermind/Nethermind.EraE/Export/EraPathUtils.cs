// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.IO.Abstractions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using EraException = Nethermind.Era1.Exceptions.EraException;
using Testably.Abstractions;

namespace Nethermind.EraE.Export;

public static class EraPathUtils
{
    private static readonly char[] _separator = ['-'];

    // Canonical e2store EraE extension, used for export.
    // See https://github.com/eth-clients/e2store-format-specs/blob/main/formats/ere.md.
    public const string FileExtension = ".ere";

    // Legacy extension still accepted on import until producers migrate to .ere.
    public const string LegacyFileExtension = ".erae";

    // Nethermind does not generate Proof entries, so exports carry the "noproofs" profile
    // postfix per the spec — matching go-ethereum's execdb writer.
    public const string NoProofsProfile = "noproofs";

    // The spec profile without CompressedSlimReceipts entries; Nethermind cannot read receipts from such a file.
    internal const string NoReceiptsProfile = "noreceipts";

    public static bool IsEraFile(string path)
    {
        ReadOnlySpan<char> extension = Path.GetExtension(path.AsSpan());
        return extension.Equals(FileExtension, StringComparison.OrdinalIgnoreCase)
            || extension.Equals(LegacyFileExtension, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCanonicalEraFile(string path) =>
        Path.GetExtension(path.AsSpan()).Equals(FileExtension, StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<string> GetAllEraFiles(string directoryPath, string network, IFileSystem fileSystem)
    {
        EnumerationOptions options = new() { RecurseSubdirectories = false, MatchCasing = MatchCasing.PlatformDefault };

        foreach (string file in fileSystem.Directory.EnumerateFiles(directoryPath, "*", options))
        {
            if (!IsEraFile(file)) continue;

            // <network>-<epoch>-<hexhash>[-<profile>...]
            string[] parts = Path.GetFileNameWithoutExtension(file).Split(_separator);
            if (parts.Length < 3 || !network.Equals(parts[0], StringComparison.OrdinalIgnoreCase)) continue;
            if (!uint.TryParse(parts[1], out _))
                throw new EraException($"Invalid era filename: {Path.GetFileName(file)}");

            yield return file;
        }
    }

    public static IEnumerable<string> GetAllEraFiles(string directoryPath, string network)
        => GetAllEraFiles(directoryPath, network, new RealFileSystem());

    public static string Filename(string network, ulong epoch, Hash256 lastBlockHash)
    {
        ArgumentException.ThrowIfNullOrEmpty(network);
        ArgumentNullException.ThrowIfNull(lastBlockHash);

        return $"{network}-{epoch:D5}-{lastBlockHash.ToString(true)[2..10]}-{NoProofsProfile}{FileExtension}";
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

    public static (long Epoch, ValueHash256 Hash) ParseChecksumEntry(string line) => ParseChecksumEntry(line, out _);

    internal static (long Epoch, ValueHash256 Hash) ParseChecksumEntry(string line, out string filename)
    {
        ValueHash256 hash = ExtractHashFromChecksumEntry(line);

        int nameStart = line.IndexOf(' ');
        if (nameStart == -1)
            throw new ArgumentException($"Invalid checksum entry (no filename): {line}");

        filename = line[(nameStart + 1)..].TrimStart();
        if (!TryGetEpoch(filename, out long epoch))
            throw new ArgumentException($"Invalid checksum entry filename: {filename}");

        return (epoch, hash);
    }

    internal static bool TryGetEpoch(string fileName, out long epoch)
    {
        string[] parts = Path.GetFileNameWithoutExtension(fileName).Split(_separator);
        epoch = 0;
        return parts.Length >= 3 && long.TryParse(parts[1], out epoch);
    }

    /// <summary>
    /// The hash part is zero: the name an export writes to before it knows the last block hash.
    /// </summary>
    internal static bool IsPlaceholderName(string path)
    {
        string[] parts = Path.GetFileNameWithoutExtension(path).Split(_separator);
        return parts.Length >= 3 && parts[2] == "00000000";
    }

    /// <summary>
    /// The profile postfixes of an era file name joined by '-', such as <c>noproofs</c>; empty for the full profile.
    /// </summary>
    internal static string GetProfile(string path)
    {
        string[] parts = Path.GetFileNameWithoutExtension(path).Split(_separator);
        return parts.Length > 3 ? string.Join('-', parts.Skip(3)) : "";
    }

    internal static bool HasProfile(string path, string profile) =>
        GetProfile(path).Split(_separator).Contains(profile, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Recognizes an era file of <paramref name="network"/> that an export may reuse or replace:
    /// <c>&lt;network&gt;-&lt;epoch&gt;-&lt;8 hex digits&gt;[-&lt;profile&gt;...]</c> with the <c>.ere</c> or legacy
    /// <c>.erae</c> extension. Stricter than <see cref="TryGetEpoch"/>, so it never matches a file that is not an era file.
    /// </summary>
    /// <param name="isPlaceholder">The hash part is zero: the file an export writes before it knows the last block hash.</param>
    internal static bool TryParseEpochFile(string path, string network, out ulong epoch, out bool isPlaceholder)
    {
        epoch = 0;
        isPlaceholder = false;
        if (!IsEraFile(path)) return false;

        string[] parts = Path.GetFileNameWithoutExtension(path).Split(_separator);
        if (parts.Length < 3 || !network.Equals(parts[0], StringComparison.OrdinalIgnoreCase)
            || !ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out epoch)
            || parts[2].Length != 8 || !parts[2].All(char.IsAsciiHexDigit)
            || parts.Skip(3).Any(static profile => profile.Length == 0 || !profile.All(char.IsAsciiLetterOrDigit)))
            return false;

        isPlaceholder = parts[2] == "00000000";
        return true;
    }
}
