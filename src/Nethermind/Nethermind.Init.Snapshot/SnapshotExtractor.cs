// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Formats.Tar;
using System.IO.Compression;
using Nethermind.Logging;
using ZstdSharp;

namespace Nethermind.Init.Snapshot;

/// <summary>
/// Extracts a snapshot archive (zip or tar-based) to a destination directory.
/// </summary>
internal sealed class SnapshotExtractor(ILogManager logManager)
{
    private readonly ILogger _logger = logManager.GetClassLogger();

    /// <summary>
    /// Extracts <paramref name="archivePath"/> into <paramref name="destinationPath"/>.
    /// For tar-based archives, <paramref name="stripComponents"/> leading path components
    /// are stripped (equivalent to <c>tar --strip-components</c>).
    /// </summary>
    public Task ExtractAsync(string archivePath, string destinationPath, int stripComponents, CancellationToken cancellationToken) =>
        Task.Run(() => Extract(archivePath, destinationPath, stripComponents, cancellationToken), cancellationToken);

    private void Extract(string archivePath, string destinationPath, int stripComponents, CancellationToken cancellationToken)
    {
        if (_logger.IsInfo)
            _logger.Info($"Extracting snapshot to {destinationPath}. Do not interrupt!");

        string extension = Path.GetExtension(archivePath).ToLowerInvariant();
        string innerExtension = Path.GetExtension(Path.GetFileNameWithoutExtension(archivePath)).ToLowerInvariant();

        if (IsZip(extension))
            ExtractZip(archivePath, destinationPath);
        else if (IsTarArchive(extension, innerExtension))
            ExtractTar(archivePath, destinationPath, extension, stripComponents, cancellationToken);
        else
            throw new NotSupportedException($"Unsupported snapshot archive format: {archivePath}");
    }

    private static bool IsZip(string extension) =>
        extension is ".zip";

    private static bool IsTarArchive(string extension, string innerExtension) =>
        extension is ".tar" or ".zst" or ".zstd" or ".gz" || innerExtension == ".tar";

    private static void ExtractZip(string archivePath, string destinationPath) =>
        ZipFile.ExtractToDirectory(archivePath, destinationPath);

    private static void ExtractTar(string archivePath, string destinationPath, string extension, int stripComponents, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationPath);

        using FileStream fileStream = File.OpenRead(archivePath);
        using Stream decompressedStream = OpenDecompressedStream(fileStream, extension);
        using TarReader tarReader = new(decompressedStream);

        string destinationRoot = destinationPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        TarEntry? entry;
        while ((entry = tarReader.GetNextEntry()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? strippedPath = StripLeadingComponents(entry.Name, stripComponents);
            if (strippedPath is null)
                continue;

            string destinationEntryPath = Path.GetFullPath(Path.Combine(destinationPath, strippedPath));

            if (!destinationEntryPath.StartsWith(destinationRoot, StringComparison.Ordinal))
                throw new IOException($"Tar entry '{entry.Name}' would extract outside the destination directory.");

            if (entry.EntryType is TarEntryType.Directory)
                Directory.CreateDirectory(destinationEntryPath);
            else
                entry.ExtractToFile(destinationEntryPath, overwrite: true);
        }
    }

    private static Stream OpenDecompressedStream(Stream fileStream, string extension) =>
        extension switch
        {
            ".zst" or ".zstd" => new DecompressionStream(fileStream),
            ".gz" => new GZipStream(fileStream, CompressionMode.Decompress),
            _ => fileStream
        };

    /// <summary>
    /// Strips <paramref name="count"/> leading path components from <paramref name="entryName"/>.
    /// Returns <c>null</c> if the entry should be skipped (not enough components).
    /// </summary>
    private static string? StripLeadingComponents(string entryName, int count)
    {
        ReadOnlySpan<char> remaining = entryName.AsSpan().TrimStart('/');

        for (int i = 0; i < count; i++)
        {
            int slash = remaining.IndexOf('/');
            if (slash < 0)
                return null;

            remaining = remaining[(slash + 1)..];
        }

        return remaining.IsEmpty ? null : remaining.ToString();
    }
}
