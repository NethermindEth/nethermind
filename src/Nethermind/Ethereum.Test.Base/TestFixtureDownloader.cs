// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;

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
    private const string MarkerFileName = ".completed";

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
    /// <returns>The path to the extracted fixtures directory.</returns>
    public static string EnsureDownloaded(string suiteName, string urlTemplate, string version, string archiveName, Func<string, bool>? shouldExtract = null, string? extractionTag = null)
    {
        string archiveStem = StripExtensions(archiveName);
        string targetDir = Path.Combine(CacheRoot, suiteName, version, archiveStem);
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
            DownloadAndExtract(urlTemplate, version, archiveName, targetDir, shouldExtract);
            File.WriteAllLines(markerPath, [extractionTag ?? version, .. PopulatedSubtrees(targetDir)]);
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

    private static void DownloadAndExtract(string urlTemplate, string version, string archiveName, string targetDir, Func<string, bool>? shouldExtract)
    {
        // Clean up any partial extraction from a previous interrupted attempt.
        if (Directory.Exists(targetDir))
            Directory.Delete(targetDir, true);

        Directory.CreateDirectory(targetDir);

        using HttpClient httpClient = new();
        string url = string.Format(urlTemplate, version, archiveName);
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        using HttpResponseMessage response = httpClient.Send(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

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

        // GZipStream and TarReader can report clean EOF after a truncated download without validating the declared length.
        if (expectedLength is { } expected && contentStream.TotalBytesRead != expected)
        {
            throw new IOException(
                $"Download of '{url}' was truncated: expected {expected} bytes but the stream yielded {contentStream.TotalBytesRead} before EOF.");
        }

        if (!HasExtractedContent(targetDir))
        {
            throw new IOException(
                $"Download of '{url}' extracted no files into '{targetDir}'" +
                (shouldExtract is null ? "." : " after filtering; the archive holds nothing the filter accepts."));
        }
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long TotalBytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            TotalBytesRead += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            int read = inner.Read(buffer);
            TotalBytesRead += read;
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
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

        using TarReader reader = new(gzStream);
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
