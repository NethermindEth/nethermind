// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Ethereum.Test.Base;

/// <summary>
/// Thread- and process-safe downloader for test fixture archives.
/// Downloads a .tar.gz archive from a URL, extracts it to a stable cache directory
/// that survives dotnet rebuilds, and uses a named mutex + completion marker to
/// prevent races across concurrent test processes.
/// </summary>
public static class TestFixtureDownloader
{
    private static readonly string CacheRoot = Path.Combine(Path.GetTempPath(), "nethermind-eest");
    internal const string MarkerFileName = ".completed";
    internal const int MaxAttempts = 5;
    internal const string TempDirectoryInfix = ".tmp-";
    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(2);

    // consensus-specs archives: tests/{preset}/{fork}/{suite}; record missing suites without walking every file.
    private const int SubtreeDepth = 4;

    /// <summary>
    /// Ensures that the archive identified by <paramref name="urlTemplate"/>,
    /// <paramref name="version"/>, and <paramref name="archiveName"/> is downloaded
    /// and extracted. Returns the path to the extraction directory.
    /// </summary>
    /// <param name="suiteName">A short label used in the cache path and mutex name (e.g. "SszTests", "PyTests").</param>
    /// <param name="urlTemplate">URL format string with {0} = version, {1} = archive name.</param>
    /// <param name="version">Archive version tag (e.g. "v1.6.1").</param>
    /// <param name="archiveName">Archive file name (e.g. "general.tar.gz"). The directory stem is derived by stripping extensions.</param>
    /// <param name="shouldExtract">Optional filter over normalized tar paths; null extracts every entry.</param>
    /// <param name="extractionTag">Cache identity for the extraction filter; a changed tag forces re-extraction.</param>
    /// <param name="expectedSha256">Optional pinned SHA-256 hex digest of the compressed archive.</param>
    /// <returns>The path to the extracted fixtures directory.</returns>
    public static string EnsureDownloaded(string suiteName, string urlTemplate, string version, string archiveName, Func<string, bool>? shouldExtract = null, string? extractionTag = null, string? expectedSha256 = null)
        => EnsureDownloaded(CacheRoot, suiteName, urlTemplate, version, archiveName, null, RetryBaseDelay, shouldExtract, extractionTag, expectedSha256);

    internal static string EnsureDownloaded(string cacheRoot, string suiteName, string urlTemplate, string version, string archiveName,
        HttpMessageHandler? handler, TimeSpan retryBaseDelay, Func<string, bool>? shouldExtract = null, string? extractionTag = null, string? expectedSha256 = null)
    {
        if (expectedSha256 is not null) extractionTag = $"{extractionTag ?? version};sha256={expectedSha256}";
        string archiveStem = StripExtensions(archiveName);
        string targetDir = Path.Combine(cacheRoot, suiteName, version, archiveStem);
        string markerPath = Path.Combine(targetDir, MarkerFileName);

        if (IsComplete(targetDir, markerPath, extractionTag))
            return targetDir;

        string mutexName = $"{suiteName}_{version}_{archiveName}".Replace('/', '_').Replace('\\', '_');
        using Mutex mutex = new(false, mutexName);
        Console.WriteLine($"Waiting for {suiteName} fixture lock ({archiveName} {version})...");
        if (!mutex.WaitOne(TimeSpan.FromMinutes(10)))
            throw new TimeoutException($"Timed out waiting for {suiteName} fixture mutex ({mutexName})");
        try
        {
            if (IsComplete(targetDir, markerPath, extractionTag))
            {
                Console.WriteLine($"{suiteName} fixtures were downloaded by another process.");
                return targetDir;
            }

            Console.WriteLine($"Downloading {suiteName} fixtures ({archiveName} {version})...");
            string url = string.Format(urlTemplate, version, archiveName);
            DeleteStaleTempDirectories(targetDir);
            DownloadWithRetries(url, extractionTag ?? version, targetDir, handler, retryBaseDelay, shouldExtract, expectedSha256);
            Console.WriteLine($"{suiteName} fixtures extracted to {targetDir}");
        }
        finally
        {
            mutex.ReleaseMutex();
        }

        return targetDir;
    }

    private static bool IsComplete(string targetDir, string markerPath, string? extractionTag)
    {
        if (!File.Exists(markerPath))
            return false;

        string[] marker = File.ReadAllLines(markerPath);
        string header = marker.Length > 0 ? marker[0] : string.Empty;
        if (extractionTag is not null && !string.Equals(header, extractionTag, StringComparison.Ordinal))
        {
            Console.WriteLine($"Ignoring completion marker written for a different extraction filter ({targetDir}); re-downloading.");
            return false;
        }

        if (marker.Length > 1)
        {
            for (int i = 1; i < marker.Length; i++)
            {
                if (HasAnyFile(Path.Combine(targetDir, marker[i])))
                    continue;

                Console.WriteLine($"Ignoring completion marker whose extracted subtree '{marker[i]}' is missing ({targetDir}); re-downloading.");
                return false;
            }
            return true;
        }

        if (HasExtractedContent(targetDir))
            return true;

        Console.WriteLine($"Ignoring completion marker over a content-free fixture directory ({targetDir}); re-downloading.");
        return false;
    }

    private static bool HasExtractedContent(string targetDir) =>
        Directory.Exists(targetDir)
        && Directory.EnumerateFiles(targetDir, "*", SearchOption.AllDirectories)
            .Any(file => !string.Equals(Path.GetFileName(file), MarkerFileName, StringComparison.Ordinal));

    private static bool HasAnyFile(string directory) =>
        Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any();

    private static List<string> PopulatedSubtrees(string targetDir)
    {
        List<string> subtrees = [];
        CollectPopulatedSubtrees(targetDir, string.Empty, SubtreeDepth, subtrees);
        return subtrees;
    }

    private static void CollectPopulatedSubtrees(string directory, string relativePath, int depth, List<string> into)
    {
        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            string childRelative = relativePath.Length == 0 ? Path.GetFileName(child) : $"{relativePath}/{Path.GetFileName(child)}";
            if (depth > 1)
                CollectPopulatedSubtrees(child, childRelative, depth - 1, into);
            else if (HasAnyFile(child))
                into.Add(childRelative);
        }
    }

    /// <summary>
    /// Strips archive extensions (e.g. ".tar.gz", ".zip") to derive the directory stem.
    /// </summary>
    private static string StripExtensions(string archiveName)
    {
        if (archiveName.EndsWith(".tar.gz", StringComparison.Ordinal))
            return archiveName[..^7];

        return Path.GetFileNameWithoutExtension(archiveName);
    }

    private static void DeleteStaleTempDirectories(string targetDir)
    {
        string parentDir = Path.GetDirectoryName(targetDir)!;
        if (!Directory.Exists(parentDir))
            return;

        foreach (string staleDir in Directory.EnumerateDirectories(parentDir, Path.GetFileName(targetDir) + TempDirectoryInfix + "*"))
            Directory.Delete(staleDir, true);
    }

    private static void DownloadWithRetries(string url, string marker, string targetDir, HttpMessageHandler? handler, TimeSpan retryBaseDelay,
        Func<string, bool>? shouldExtract, string? expectedSha256)
    {
        using HttpClient httpClient = handler is null ? new() : new(handler, disposeHandler: false);
        for (int attempt = 1; ; attempt++)
        {
            string tempDir = targetDir + TempDirectoryInfix + Guid.NewGuid().ToString("N");
            try
            {
                DownloadAndExtract(httpClient, url, tempDir, shouldExtract, expectedSha256);
                File.WriteAllLines(Path.Combine(tempDir, MarkerFileName), [marker, .. PopulatedSubtrees(tempDir)]);
                DeleteIfExists(targetDir);
                Directory.Move(tempDir, targetDir);
                return;
            }
            catch (Exception exception) when (IsTransient(exception))
            {
                DeleteIfExists(tempDir);
                if (attempt >= MaxAttempts)
                    throw new HttpRequestException($"Failed to download test fixtures from {url} after {MaxAttempts} attempts.", exception,
                        (exception as HttpRequestException)?.StatusCode);

                TimeSpan delay = retryBaseDelay * (1 << (attempt - 1));
                Console.WriteLine($"Attempt {attempt} of {MaxAttempts} to download {url} failed ({exception.Message}). Retrying in {delay.TotalSeconds:0.#} s.");
                Thread.Sleep(delay);
            }
            catch
            {
                DeleteIfExists(tempDir);
                throw;
            }
        }
    }

    private static bool IsTransient(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: HttpStatusCode status } => status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500,
        TaskCanceledException { InnerException: TimeoutException } => true,
        HttpIOException or EndOfStreamException or InvalidDataException => true,
        _ => false
    };

    private static void DeleteIfExists(string directory)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }

    private static void DownloadAndExtract(HttpClient httpClient, string url, string targetDir, Func<string, bool>? shouldExtract, string? expectedSha256)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        using HttpResponseMessage response = httpClient.Send(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        Directory.CreateDirectory(targetDir);

        long? expectedLength = response.Content.Headers.ContentLength;
        using CountingStream contentStream = new(response.Content.ReadAsStream());
        using GZipStream gzStream = new(contentStream, CompressionMode.Decompress);

        if (shouldExtract is null)
        {
            TarFile.ExtractToDirectory(gzStream, targetDir, overwriteFiles: true);
        }
        else
        {
            ExtractSelective(gzStream, targetDir, shouldExtract);
        }

        // Tar EOF can precede gzip EOF when an archive has trailing padding.
        gzStream.CopyTo(Stream.Null);
        contentStream.CopyTo(Stream.Null);

        // GZipStream can report clean EOF after a truncated download without validating the declared length.
        if (expectedLength is { } expected && contentStream.TotalBytesRead != expected)
        {
            throw new EndOfStreamException(
                $"Download of '{url}' was truncated: expected {expected} bytes but the stream yielded {contentStream.TotalBytesRead} before EOF.");
        }

        if (expectedSha256 is not null && !string.Equals(contentStream.Digest(), expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SHA-256 mismatch for fixture archive {url}.");

        if (!HasExtractedContent(targetDir))
        {
            throw new IOException(
                $"Download of '{url}' extracted no files into '{targetDir}'" +
                (shouldExtract is null ? "." : " after filtering; the archive holds nothing the filter accepts."));
        }
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public string Digest() => Convert.ToHexString(_hash.GetHashAndReset());
        public long TotalBytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            try
            {
                int read = inner.Read(buffer);
                _hash.AppendData(buffer[..read]);
                TotalBytesRead += read;
                return read;
            }
            catch (IOException exception) when (exception is not HttpIOException)
            {
                throw new HttpIOException(HttpRequestError.ResponseEnded, exception.Message, exception);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // .tar.gz has no seek index, so selective extraction saves disk space and time, not network transfer.
    private static void ExtractSelective(Stream gzStream, string targetDir, Func<string, bool> shouldExtract)
    {
        string targetRoot = Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar;

        using TarReader reader = new(gzStream, leaveOpen: true);
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is TarEntryType.Directory or TarEntryType.GlobalExtendedAttributes)
                continue;

            string normalized = NormalizeEntryPath(entry.Name);
            if (!shouldExtract(normalized))
                continue;

            string destinationPath = Path.GetFullPath(Path.Combine(targetDir, normalized));
            if (!destinationPath.StartsWith(targetRoot, StringComparison.Ordinal))
                throw new IOException($"Tar entry '{entry.Name}' would extract outside the target directory.");

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, overwrite: true);
        }
    }

    private static string NormalizeEntryPath(string entryName)
    {
        string path = entryName.Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];
        return path.TrimStart('/');
    }

    /// <summary>Matches the prefix itself or a descendant at a slash boundary, accepting either slash direction and a leading "./".</summary>
    public static bool PathUnderPrefix(string entryPath, string prefix)
    {
        string normalizedEntry = NormalizeEntryPath(entryPath);
        string normalizedPrefix = NormalizeEntryPath(prefix).TrimEnd('/');

        return normalizedEntry.Length == normalizedPrefix.Length
            ? string.Equals(normalizedEntry, normalizedPrefix, StringComparison.Ordinal)
            : normalizedEntry.Length > normalizedPrefix.Length
              && normalizedEntry.StartsWith(normalizedPrefix, StringComparison.Ordinal)
              && normalizedEntry[normalizedPrefix.Length] == '/';
    }
}
