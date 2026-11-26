// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.IO.Abstractions;
using System.Security.Cryptography;
using Autofac.Features.AttributeFilters;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Init.Steps;
using Nethermind.Logging;

namespace Nethermind.Init.Snapshot;

/// <summary>
/// Optionally bootstraps the database from a remote snapshot before the node starts.
/// The download is resumable and idempotent: a checkpoint file tracks progress so that
/// restarts skip already-completed stages.
/// </summary>
[RunnerStepDependencies(
    dependencies: [],
    dependents: [typeof(InitializeBlockTree), typeof(DatabaseMigrations), typeof(StartLogIndex)])]
public class InitDatabaseSnapshot(
    INethermindApi api,
    [KeyFilter(nameof(IInitConfig.BaseDbPath))] IDriveInfo[] drives) : IStep
{
    private const int MaxStreamingConnections = 16;
    private const int InitialRetryDelaySeconds = 5;
    private const int MaxRetryDelaySeconds = 300;
    private const int ChecksumBufferSize = 65536;
    private const int ChecksumProgressIntervalSeconds = 30;
    private const string LockFileSuffix = ".snapshot.lock";

    private readonly ILogger _logger = api.LogManager.GetClassLogger<InitDatabaseSnapshot>();

    public async Task Execute(CancellationToken cancellationToken)
    {
        if (!IsInMemoryOrReadOnlyMode())
            await InitDbFromSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool IsInMemoryOrReadOnlyMode() =>
        api.Config<IInitConfig>().DiagnosticMode is
            DiagnosticMode.RpcDb or
            DiagnosticMode.ReadOnlyDb or
            DiagnosticMode.MemDb;

    private async Task InitDbFromSnapshotAsync(CancellationToken cancellationToken)
    {
        ISnapshotConfig snapshotConfig = api.Config<ISnapshotConfig>();
        string dbPath = api.Config<IInitConfig>().BaseDbPath;
        string snapshotUrl = snapshotConfig.DownloadUrl
            ?? throw new InvalidOperationException("Snapshot download URL is not configured.");
        string snapshotPath = Path.Combine(snapshotConfig.SnapshotDirectory, snapshotConfig.SnapshotFileName);

        if (snapshotConfig.StripComponents < 0)
            throw new InvalidOperationException($"Snapshot.StripComponents must be non-negative, got {snapshotConfig.StripComponents}.");

        if (snapshotConfig.Streaming && snapshotConfig.StreamingConnections is < 1 or > MaxStreamingConnections)
            throw new InvalidOperationException($"Snapshot.StreamingConnections must be between 1 and {MaxStreamingConnections}, got {snapshotConfig.StreamingConnections}.");

        // Held until this method returns, on every path: before the checkpoint, the marker or the database is read.
        using FileStream? databaseLock = LockDatabase(dbPath);
        SnapshotCheckpoint checkpoint = new(snapshotConfig, api.LogManager);

        // This step deletes nothing in the database directory but its own marker after success. The checkpoint lives in the
        // snapshot directory, which can change or be lost, and cannot tell a partial extraction from a synced database; the
        // marker shows one, and stays until the operator has emptied the directory. The content is read first: an
        // extraction creates its marker before its first file, so a start that saw its files sees its marker too.
        bool populated = SnapshotDatabase.Exists(dbPath);
        if (SnapshotDatabase.HasUnfinishedExtraction(dbPath))
            throw new IOException(SnapshotDatabase.UnfinishedExtraction(dbPath));

        if (populated)
        {
            if (checkpoint.Read() < SnapshotStage.Extracted)
            {
                if (_logger.IsWarn)
                    _logger.Warn($"The database directory {dbPath:hide} holds content and no snapshot extraction marker, so it is kept and snapshot initialization is skipped. To initialize from the snapshot instead, empty the directory first. A partial extraction left by an older version looks the same.");
            }
            else if (_logger.IsInfo)
            {
                _logger.Info($"Database already exists at {dbPath:hide}. Skipping snapshot initialization.");
            }

            return;
        }

        // The archive, the checkpoint and its temporary file go into the snapshot directory, so it must lie outside the
        // database directory, which the extraction claims only while it is empty, and the file name must name a file in it.
        if (!IsPlainFileName(snapshotConfig.SnapshotFileName))
            throw new InvalidOperationException($"Snapshot.SnapshotFileName must be a plain file name, without a directory, got '{(SensitiveLogMasking.Enabled ? "[redacted]" : snapshotConfig.SnapshotFileName)}'.");
        if (IsWithin(snapshotConfig.SnapshotDirectory, dbPath))
            throw new InvalidOperationException($"Snapshot.SnapshotDirectory ({(SensitiveLogMasking.Enabled ? "[redacted]" : snapshotConfig.SnapshotDirectory)}) is the database directory at {(SensitiveLogMasking.Enabled ? "[redacted]" : dbPath)} or lies inside it, and the snapshot extraction must find that directory empty. Nothing was downloaded. Set Snapshot.SnapshotDirectory to a directory outside the database directory, then restart.");
        Directory.CreateDirectory(snapshotConfig.SnapshotDirectory);

        if (checkpoint.Read() >= SnapshotStage.Extracted)
        {
            if (_logger.IsWarn)
                _logger.Warn($"The snapshot checkpoint indicates a completed extraction, but the database at {dbPath:hide} is missing or empty. Reinitializing from the snapshot.");
            checkpoint.Advance(File.Exists(snapshotPath) ? SnapshotStage.Downloaded : SnapshotStage.Started);
        }

        if (checkpoint.Read() >= SnapshotStage.Downloaded && !File.Exists(snapshotPath))
        {
            if (_logger.IsWarn)
                _logger.Warn($"The snapshot checkpoint indicates a completed download, but no archive exists at {snapshotPath:hide}. Restarting the download.");
            checkpoint.Advance(SnapshotStage.Started);
        }

        if (snapshotConfig.Streaming)
        {
            if (checkpoint.Read() < SnapshotStage.Downloaded)
            {
                StreamingSnapshotInitializer initializer = new(
                    snapshotConfig, snapshotUrl, dbPath, drives,
                    SnapshotStreamSettings.Default(snapshotConfig.StreamingConnections), api.LogManager);
                await initializer.InitializeAsync(checkpoint, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (_logger.IsWarn)
                _logger.Warn($"A fully downloaded snapshot archive exists at {snapshotPath:hide}. Extracting it instead of streaming.");
        }

        using SnapshotDownloader downloader = new(api.LogManager);
        await DownloadWithRetryAsync(downloader, snapshotUrl, snapshotPath, checkpoint, cancellationToken).ConfigureAwait(false);

        bool checksumPassed = await VerifyChecksumAsync(snapshotPath, snapshotConfig, checkpoint, cancellationToken).ConfigureAwait(false);
        if (!checksumPassed)
        {
            if (_logger.IsWarn)
                _logger.Warn($"Deleting invalid snapshot file '{snapshotPath:hide}' and resetting checkpoint for re-download on next run.");
            File.Delete(snapshotPath);
            checkpoint.Advance(SnapshotStage.Started);
            return;
        }

        await ExtractAsync(snapshotPath, dbPath, snapshotConfig.StripComponents, checkpoint, cancellationToken).ConfigureAwait(false);

        if (_logger.IsInfo)
        {
            _logger.Info("Database successfully initialized from snapshot.");
            _logger.Info($"Deleting snapshot file {snapshotPath:hide}.");
        }

        File.Delete(snapshotPath);
        checkpoint.Advance(SnapshotStage.Completed);
    }

    /// <summary>
    /// Keeps a second start on the same database path out until this one returns. It would otherwise download into the
    /// same archive and checkpoint while this one works; the marker alone keeps it from extracting into the same
    /// directory. The lock is a file next to the database directory, held open with <see cref="FileShare.None"/>: on Unix
    /// an advisory flock, which the kernel releases when the process exits. The file is never deleted: a process that
    /// opened it just before would then hold its lock on a file that the next start no longer finds.
    /// </summary>
    private FileStream? LockDatabase(string dbPath)
    {
        // As configured, links not resolved: a link and its target are two paths, with two locks.
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dbPath));
        string? parent = Path.GetDirectoryName(fullPath);
        if (parent is null)
        {
            // Not refused: such a database started before the lock existed.
            if (_logger.IsWarn)
                _logger.Warn($"The database directory {dbPath:hide} is a filesystem root, which has no place outside it for a lock file, so a second process starting on it at the same time is not kept out of the snapshot initialization.");
            return null;
        }

        string lockPath = fullPath + LockFileSuffix;
        // Opening a link would create or lock whatever it leads to, a file in the database directory included.
        if (new FileInfo(lockPath).LinkTarget is not null)
        {
            if (_logger.IsWarn)
                _logger.Warn($"The lock file {lockPath:hide} is a link, which the step does not follow, so a second process starting on the database at {dbPath:hide} at the same time is not kept out of the snapshot initialization.");
            return null;
        }

        try
        {
            Directory.CreateDirectory(parent);
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 0);
        }
        catch (IOException e) when (IsHeldElsewhere(e))
        {
            throw new IOException(
                $"Another process is initializing the database at {(SensitiveLogMasking.Enabled ? "[redacted]" : dbPath)} from a snapshot: it holds the lock file {(SensitiveLogMasking.Enabled ? "[redacted]" : lockPath)}. Let it finish, then restart.",
                SensitiveLogMasking.Enabled ? null : e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Such as a parent directory the node may not write to: a node that started before the lock existed still starts.
            if (_logger.IsWarn)
                _logger.Warn($"Could not take the lock file {lockPath:hide}, so a second process starting on the database at {dbPath:hide} at the same time is not kept out of the snapshot initialization. Error: {(SensitiveLogMasking.Enabled ? e.GetType().Name : e.Message)}");
            return null;
        }
    }

    // No directory part, so not rooted either.
    private static bool IsPlainFileName(string name) => name is not ("" or "." or "..") && Path.GetFileName(name) == name;

    // Whether the path is the directory or lies inside it, comparing full paths as configured: links are not resolved, and
    // only whole path components match. Letter case is ignored on Windows and macOS, whose filesystems usually ignore it.
    // A case-insensitive mount on Linux or another alias of the same directory (a link, a bind mount) is not detected: the
    // snapshot directory must be physically separate from the database directory.
    private static bool IsWithin(string path, string directory)
    {
        StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return string.Equals(fullPath, fullDirectory, comparison)
            || fullPath.StartsWith(Path.EndsInDirectorySeparator(fullDirectory) ? fullDirectory : fullDirectory + Path.DirectorySeparatorChar, comparison);
    }

    // How a FileShare.None handle held elsewhere is reported: ERROR_SHARING_VIOLATION on Windows, and on Unix the errno
    // EWOULDBLOCK of the flock behind it (11 on Linux, 35 on macOS). A permission error is an UnauthorizedAccessException.
    private static bool IsHeldElsewhere(IOException e) =>
        e.HResult == (OperatingSystem.IsWindows() ? unchecked((int)0x80070020) : OperatingSystem.IsLinux() ? 11 : 35);

    private async Task DownloadWithRetryAsync(
        SnapshotDownloader downloader, string url, string destinationPath, SnapshotCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        if (checkpoint.Read() >= SnapshotStage.Downloaded)
            return;

        await CheckDiskSpaceBeforeDownloadAsync(downloader, url, destinationPath, cancellationToken).ConfigureAwait(false);

        TimeSpan retryDelay = TimeSpan.FromSeconds(InitialRetryDelaySeconds);
        long lastSize = GetFileSize(destinationPath);

        while (true)
        {
            try
            {
                await downloader.DownloadAsync(url, destinationPath, cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (HttpRequestException e) when (SnapshotHttpClient.IsPermanentHttpError(e))
            {
                if (_logger.IsError)
                    _logger.Error($"Snapshot download failed with permanent HTTP error {(int?)e.StatusCode}. Aborting.");
                throw;
            }
            catch (Exception e) when (e is IOException or HttpRequestException)
            {
                long currentSize = GetFileSize(destinationPath);
                if (currentSize > lastSize)
                    retryDelay = TimeSpan.FromSeconds(InitialRetryDelaySeconds);
                lastSize = currentSize;

                if (_logger.IsError)
                    _logger.Error($"Snapshot download failed. Retrying in {retryDelay.TotalSeconds}s. Error: {(SensitiveLogMasking.Enabled ? e.GetType().Name : e.ToString())}");
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, MaxRetryDelaySeconds));
            }
        }

        checkpoint.Advance(SnapshotStage.Downloaded);
    }

    private long GetFileSize(string path)
    {
        IFileInfo file = api.FileSystem.FileInfo.New(path);
        return file.Exists ? file.Length : 0;
    }

    private async Task<bool> VerifyChecksumAsync(
        string snapshotPath, ISnapshotConfig config, SnapshotCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        if (checkpoint.Read() >= SnapshotStage.Verified)
            return true;

        if (config.Checksum is not null)
        {
            if (_logger.IsInfo)
                _logger.Info($"Verifying snapshot checksum {config.Checksum}.");

            byte[] actual = await ComputeChecksumAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
            if (!SnapshotChecksum.Verify(actual, config.Checksum, "Aborting snapshot initialization, but the node will continue running.", _logger))
                return false;
        }
        else if (_logger.IsWarn)
        {
            _logger.Warn("Snapshot checksum is not configured.");
        }

        checkpoint.Advance(SnapshotStage.Verified);
        return true;
    }

    private async Task ExtractAsync(
        string snapshotPath, string dbPath, int stripComponents, SnapshotCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        if (checkpoint.Read() >= SnapshotStage.Extracted)
            return;

        SnapshotDiskSpace.Check(drives, SnapshotDiskSpace.GetRequiredSpaceForExtraction(GetFileSize(snapshotPath)), "extract");

        SnapshotDatabase.BeginExtraction(dbPath);
        SnapshotExtractor extractor = new(api.LogManager);
        try
        {
            await extractor.ExtractAsync(snapshotPath, dbPath, stripComponents, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Nothing is deleted: the marker keeps every later start from taking the partial extraction for a database.
            if (_logger.IsError)
                _logger.Error($"Snapshot extraction did not finish. {SnapshotDatabase.UnfinishedExtraction(dbPath)}");
            throw;
        }

        // The marker goes first: a stop or a failed checkpoint write after it leaves a complete database, which the next
        // start keeps, rather than a complete database that every later start refuses.
        SnapshotDatabase.CompleteExtraction(dbPath);
        checkpoint.Advance(SnapshotStage.Extracted);
    }

    private async Task CheckDiskSpaceBeforeDownloadAsync(
        SnapshotDownloader downloader, string url, string destinationPath, CancellationToken cancellationToken)
    {
        long existingSize = GetFileSize(destinationPath);
        long? totalSize;
        try
        {
            totalSize = (await downloader.ProbeAsync(url, cancellationToken).ConfigureAwait(false)).Length;
        }
        catch (Exception e) when (e is IOException or HttpRequestException)
        {
            if (_logger.IsWarn)
                _logger.Warn($"Could not determine the snapshot size upfront. Skipping the pre-download disk space check. Error: {(SensitiveLogMasking.Enabled ? e.GetType().Name : e.Message)}");
            return;
        }

        if (totalSize is null)
        {
            if (_logger.IsWarn)
                _logger.Warn("The server did not report the snapshot size. Skipping the pre-download disk space check.");
            return;
        }

        SnapshotDiskSpace.Check(drives, SnapshotDiskSpace.GetRequiredSpaceForDownload(totalSize.Value, existingSize), "download and extract");
    }

    private async Task<byte[]> ComputeChecksumAsync(string filePath, CancellationToken cancellationToken)
    {
        long fileSize = new FileInfo(filePath).Length;
        using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ChecksumBufferSize);
        await using FileStream fileStream = new(filePath, FileMode.Open, FileAccess.Read,
            FileShare.None, bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        long bytesHashed = 0;
        DateTime nextLog = DateTime.UtcNow.AddSeconds(ChecksumProgressIntervalSeconds);

        int bytesRead;
        while ((bytesRead = await fileStream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            hasher.AppendData(buffer, 0, bytesRead);
            bytesHashed += bytesRead;

            if (_logger.IsInfo && fileSize > 0 && DateTime.UtcNow >= nextLog)
            {
                _logger.Info($"Snapshot checksum progress: {bytesHashed * 100 / fileSize}%");
                nextLog = DateTime.UtcNow.AddSeconds(ChecksumProgressIntervalSeconds);
            }
        }

        ArrayPool<byte>.Shared.Return(buffer);
        return hasher.GetHashAndReset();
    }
}
