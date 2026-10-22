// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
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

    /// <summary>
    /// Ensures that the archive identified by <paramref name="urlTemplate"/>,
    /// <paramref name="version"/>, and <paramref name="archiveName"/> is downloaded
    /// and extracted. Returns the path to the extraction directory.
    /// </summary>
    /// <param name="suiteName">A short label used in the cache path and mutex name (e.g. "SszTests", "PyTests").</param>
    /// <param name="urlTemplate">URL format string with {0} = version, {1} = archive name.</param>
    /// <param name="version">Archive version tag (e.g. "v1.6.1").</param>
    /// <param name="archiveName">Archive file name (e.g. "general.tar.gz"). The directory stem is derived by stripping extensions.</param>
    /// <returns>The path to the extracted fixtures directory.</returns>
    public static string EnsureDownloaded(string suiteName, string urlTemplate, string version, string archiveName)
    {
        string archiveStem = StripExtensions(archiveName);
        string targetDir = Path.Combine(CacheRoot, suiteName, version, archiveStem);
        string markerPath = Path.Combine(targetDir, ".completed");

        if (File.Exists(markerPath))
            return targetDir;

        string mutexName = $"{suiteName}_{version}_{archiveName}".Replace('/', '_').Replace('\\', '_');
        using Mutex mutex = new(false, mutexName);
        Console.WriteLine($"Waiting for {suiteName} fixture lock ({archiveName} {version})...");
        if (!mutex.WaitOne(TimeSpan.FromMinutes(10)))
            throw new TimeoutException($"Timed out waiting for {suiteName} fixture mutex ({mutexName})");
        try
        {
            if (File.Exists(markerPath))
            {
                Console.WriteLine($"{suiteName} fixtures were downloaded by another process.");
                return targetDir;
            }

            Console.WriteLine($"Downloading {suiteName} fixtures ({archiveName} {version})...");
            DownloadAndExtract(urlTemplate, version, archiveName, targetDir);
            File.WriteAllText(markerPath, version);
            Console.WriteLine($"{suiteName} fixtures extracted to {targetDir}");
        }
        finally
        {
            mutex.ReleaseMutex();
        }

        return targetDir;
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

    private static void DownloadAndExtract(string urlTemplate, string version, string archiveName, string targetDir)
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

        using Stream contentStream = response.Content.ReadAsStream();
        using GZipStream gzStream = new(contentStream, CompressionMode.Decompress);

        TarFile.ExtractToDirectory(gzStream, targetDir, overwriteFiles: true);
    }
}
