// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.IO.Compression;
using Nethermind.Logging;

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
        Task.Run(() => Extract(archivePath, destinationPath, stripComponents), cancellationToken);

    private void Extract(string archivePath, string destinationPath, int stripComponents)
    {
        if (_logger.IsInfo)
            _logger.Info($"Extracting snapshot to {destinationPath}. Do not interrupt!");

        string extension = Path.GetExtension(archivePath).ToLowerInvariant();
        string innerExtension = Path.GetExtension(Path.GetFileNameWithoutExtension(archivePath)).ToLowerInvariant();

        if (IsZip(extension))
            ExtractZip(archivePath, destinationPath);
        else if (IsTarArchive(extension, innerExtension))
            ExtractTar(archivePath, destinationPath, stripComponents);
        else
            throw new NotSupportedException($"Unsupported snapshot archive format: {archivePath}");
    }

    private static bool IsZip(string extension) =>
        extension is ".zip";

    private static bool IsTarArchive(string extension, string innerExtension) =>
        extension is ".zst" or ".zstd" or ".gz" or ".bz2" or ".xz" || innerExtension == ".tar";

    private static void ExtractZip(string archivePath, string destinationPath) =>
        ZipFile.ExtractToDirectory(archivePath, destinationPath);

    private static void ExtractTar(string archivePath, string destinationPath, int stripComponents)
    {
        Directory.CreateDirectory(destinationPath);

        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "tar",
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };
        process.StartInfo.ArgumentList.Add("--extract");
        process.StartInfo.ArgumentList.Add("--file");
        process.StartInfo.ArgumentList.Add(archivePath);
        process.StartInfo.ArgumentList.Add("--directory");
        process.StartInfo.ArgumentList.Add(destinationPath);
        process.StartInfo.ArgumentList.Add($"--strip-components={stripComponents}");

        process.Start();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new IOException($"tar extraction failed (exit {process.ExitCode}): {stderr}");
    }
}
