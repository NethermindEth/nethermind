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

    /// <summary>
    /// Path depth at which extracted subtrees are recorded in the marker: <c>tests/{preset}/{fork}/{suite}</c>
    /// in the consensus-specs archives, so a missing fork or suite is caught without walking every file.
    /// </summary>
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
    /// <param name="shouldExtract">
    /// Optional entry filter. When given, only tar entries whose normalized (forward-slash) path
    /// satisfies the predicate are written to disk; everything else is skipped without touching the
    /// filesystem. The gzip stream is still read and decompressed end to end regardless, since a
    /// .tar.gz cannot be seeked - this saves disk footprint and extraction time, not download bandwidth.
    /// Omit it (or pass null) to extract every entry, as before.
    /// </param>
    /// <param name="extractionTag">
    /// Identifies what <paramref name="shouldExtract"/> keeps. It is written into the completion
    /// marker's first line, and a cached marker carrying a different tag is treated as absent, so
    /// widening the filter re-downloads instead of leaving the new subtrees silently missing from a
    /// "complete" cache. Callers that extract everything can leave it null; their marker's first line
    /// then holds the version, as it always has, and is not checked.
    /// </param>
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

    /// <summary>
    /// A marker over an emptied or never-populated directory was seen in the wild, and every suite over
    /// it ran zero vectors and passed; only a marker over at least one real file counts as complete.
    /// A marker written for a different extraction filter is equally hollow for the subtrees the
    /// current filter keeps, so it is refused too when a tag is given. A partially emptied cache is
    /// just as hollow for the subtrees it lost, so every subtree the marker recorded must still hold
    /// a file; a marker that predates subtree recording falls back to the whole-directory check.
    /// </summary>
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

    /// <summary>True when something other than the marker itself exists under <paramref name="targetDir"/>. Stops at the first hit, so it is cheap even on a 200k-file cache.</summary>
    private static bool HasExtractedContent(string targetDir) =>
        Directory.Exists(targetDir)
        && Directory.EnumerateFiles(targetDir, "*", SearchOption.AllDirectories)
            .Any(file => !string.Equals(Path.GetFileName(file), MarkerFileName, StringComparison.Ordinal));

    private static bool HasAnyFile(string directory) =>
        Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any();

    /// <summary>
    /// The directories <see cref="SubtreeDepth"/> levels below <paramref name="targetDir"/> that hold at
    /// least one file, as forward-slash relative paths; an archive too shallow to have any records none.
    /// </summary>
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
        // ResponseHeadersRead + a plain stream read below mean the response body is never buffered
        // into a byte[]; both the "extract everything" and selective paths decompress in a bounded
        // window as bytes arrive off the wire, regardless of the archive's total size.
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

        // A dropped connection can leave GZipStream reporting a clean end-of-stream on a truncated
        // body instead of a CRC/length error, and TarReader then just sees "no more entries" - so a
        // partial archive silently looks complete and gets marked done. Checking bytes actually read
        // against Content-Length (when the server sent one) catches that before the marker is written.
        if (expectedLength is { } expected && contentStream.TotalBytesRead != expected)
        {
            throw new IOException(
                $"Download of '{url}' was truncated: expected {expected} bytes but the stream yielded {contentStream.TotalBytesRead} before EOF.");
        }

        // An archive that unpacks to nothing (filter matched no entry, hollowed-out asset, bare directories)
        // must not be marked complete, or every suite over it enumerates zero vectors and passes.
        if (!HasExtractedContent(targetDir))
        {
            throw new IOException(
                $"Download of '{url}' extracted no files into '{targetDir}'" +
                (shouldExtract is null ? "." : " after filtering; the archive holds nothing the filter accepts."));
        }
    }

    /// <summary>Wraps a stream to track how many bytes were actually read off it, so a truncated download can be detected even when the decompressor itself does not notice.</summary>
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

    /// <summary>
    /// Streams tar entries one at a time and writes to disk only those <paramref name="shouldExtract"/>
    /// accepts. The whole gzip stream is still decompressed sequentially - a .tar.gz has no index to
    /// seek by - so this trades disk footprint and unpack time for the entries that are skipped, not
    /// network transfer.
    /// </summary>
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

    /// <summary>
    /// Normalizes a tar entry's recorded path to a rooted-relative, forward-slash form: strips a
    /// leading "./" (common on GNU tar output) and converts backslashes, without touching disk.
    /// </summary>
    private static string NormalizeEntryPath(string entryName)
    {
        string path = entryName.Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];
        return path.TrimStart('/');
    }

    /// <summary>
    /// Pure predicate for use as <see cref="EnsureDownloaded"/>'s <c>shouldExtract</c> filter: true when
    /// <paramref name="entryPath"/> is <paramref name="prefix"/> itself or lies under it as a directory
    /// (matches on a '/' boundary, not merely a common string prefix, and tolerates either slash
    /// direction and a leading "./" in <paramref name="entryPath"/>). Needs no archive or filesystem
    /// access, so it is testable on its own.
    /// </summary>
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
