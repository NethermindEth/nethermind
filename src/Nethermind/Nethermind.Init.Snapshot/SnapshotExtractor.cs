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
    private readonly ILogger _logger = logManager.GetClassLogger<SnapshotExtractor>();

    /// <summary>
    /// Extracts <paramref name="archivePath"/> into <paramref name="destinationPath"/>.
    /// For tar-based archives, <paramref name="stripComponents"/> leading path components
    /// are stripped (equivalent to <c>tar --strip-components</c>).
    /// </summary>
    public Task ExtractAsync(string archivePath, string destinationPath, int stripComponents, CancellationToken cancellationToken) =>
        Task.Run(() => Extract(archivePath, destinationPath, stripComponents, cancellationToken), cancellationToken);

    public Task ExtractTarStreamAsync(Stream archiveStream, string destinationPath, string extension, int stripComponents, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            if (_logger.IsInfo)
                _logger.Info($"Extracting streamed snapshot to {destinationPath}. Do not interrupt!");

            Stream decompressedStream = OpenDecompressedStream(archiveStream, extension, leaveOpen: true);
            try
            {
                ExtractTarEntries(decompressedStream, destinationPath, stripComponents, cancellationToken);
            }
            finally
            {
                if (!ReferenceEquals(decompressedStream, archiveStream))
                    decompressedStream.Dispose();
            }
        }, cancellationToken);

    private void Extract(string archivePath, string destinationPath, int stripComponents, CancellationToken cancellationToken)
    {
        if (_logger.IsInfo)
            _logger.Info($"Extracting snapshot to {destinationPath}. Do not interrupt!");

        string extension = Path.GetExtension(archivePath).ToLowerInvariant();
        string innerExtension = Path.GetExtension(Path.GetFileNameWithoutExtension(archivePath)).ToLowerInvariant();

        if (IsZip(extension))
            ExtractZip(archivePath, destinationPath, cancellationToken);
        else if (IsTarArchive(extension, innerExtension))
            ExtractTar(archivePath, destinationPath, extension, stripComponents, cancellationToken);
        else
            throw new NotSupportedException($"Unsupported snapshot archive format: {archivePath}");
    }

    private static bool IsZip(string extension) =>
        extension is ".zip";

    private static bool IsTarArchive(string extension, string innerExtension) =>
        extension is ".tar" or ".zst" or ".zstd" or ".gz" or ".bz2" or ".xz" || innerExtension == ".tar";

    private static void ExtractZip(string archivePath, string destinationPath, CancellationToken cancellationToken)
    {
        // ZipFile.ExtractToDirectory has no overload accepting a CancellationToken,
        // so check before entering the uncooperative call.
        cancellationToken.ThrowIfCancellationRequested();
        ZipFile.ExtractToDirectory(archivePath, destinationPath);
    }

    private static void ExtractTar(string archivePath, string destinationPath, string extension, int stripComponents, CancellationToken cancellationToken)
    {
        using FileStream fileStream = File.OpenRead(archivePath);
        using Stream decompressedStream = OpenDecompressedStream(fileStream, extension, leaveOpen: false);
        ExtractTarEntries(decompressedStream, destinationPath, stripComponents, cancellationToken);
    }

    private static void ExtractTarEntries(Stream decompressedStream, string destinationPath, int stripComponents, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationPath);

        using TarReader tarReader = new(decompressedStream, leaveOpen: true);

        string destinationRoot = destinationPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        TarEntry? entry;
        while ((entry = tarReader.GetNextEntry()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.EntryType is TarEntryType.GlobalExtendedAttributes)
                continue;

            string? strippedPath = StripLeadingComponents(entry.Name, stripComponents);
            if (strippedPath is null)
                continue;

            string destinationEntryPath = Path.GetFullPath(Path.Combine(destinationPath, strippedPath));

            if (!destinationEntryPath.StartsWith(destinationRoot, StringComparison.Ordinal))
                throw new IOException($"Tar entry '{entry.Name}' would extract outside the destination directory.");

            if (entry.EntryType is TarEntryType.Directory)
                Directory.CreateDirectory(destinationEntryPath);
            else if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                entry.ExtractToFile(destinationEntryPath, overwrite: true);
            else
                throw new IOException($"Tar entry '{entry.Name}' has unsupported type {entry.EntryType}.");
        }
    }

    private static Stream OpenDecompressedStream(Stream archiveStream, string extension, bool leaveOpen) =>
        extension switch
        {
            ".zst" or ".zstd" => new DecompressionStream(archiveStream, leaveOpen: leaveOpen),
            ".gz" => new GZipStream(archiveStream, CompressionMode.Decompress, leaveOpen),
            // .bz2 and .xz are matched by IsTarArchive but have no decompression support in .NET BCL.
            ".bz2" or ".xz" => throw new NotSupportedException($"Tar compression format '{extension}' is not supported. Use .gz or .zst instead."),
            _ => archiveStream
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
