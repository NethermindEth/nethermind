// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
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
    internal const int MaxAttempts = 5;
    internal const string MarkerFileName = ".completed";
    internal const string TempDirectoryInfix = ".tmp-";

    private static readonly string CacheRoot = Path.Combine(Path.GetTempPath(), "nethermind-eest");
    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(2);

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
    public static string EnsureDownloaded(string suiteName, string urlTemplate, string version, string archiveName) =>
        EnsureDownloaded(CacheRoot, suiteName, urlTemplate, version, archiveName, null, RetryBaseDelay);

    internal static string EnsureDownloaded(
        string cacheRoot,
        string suiteName,
        string urlTemplate,
        string version,
        string archiveName,
        HttpMessageHandler? handler,
        TimeSpan retryBaseDelay)
    {
        string archiveStem = StripExtensions(archiveName);
        string targetDir = Path.Combine(cacheRoot, suiteName, version, archiveStem);
        string markerPath = Path.Combine(targetDir, MarkerFileName);

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
            string url = string.Format(urlTemplate, version, archiveName);
            DeleteStaleTempDirectories(targetDir);
            DownloadWithRetries(url, version, targetDir, handler, retryBaseDelay);
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

    private static void DeleteStaleTempDirectories(string targetDir)
    {
        string parentDir = Path.GetDirectoryName(targetDir)!;
        if (!Directory.Exists(parentDir))
            return;

        foreach (string staleDir in Directory.EnumerateDirectories(parentDir, Path.GetFileName(targetDir) + TempDirectoryInfix + "*"))
            Directory.Delete(staleDir, true);
    }

    private static void DownloadWithRetries(string url, string version, string targetDir, HttpMessageHandler? handler, TimeSpan retryBaseDelay)
    {
        using HttpClient httpClient = handler is null ? new() : new(handler, disposeHandler: false);

        for (int attempt = 1; ; attempt++)
        {
            string tempDir = targetDir + TempDirectoryInfix + Guid.NewGuid().ToString("N");
            try
            {
                DownloadAndExtract(httpClient, url, tempDir);
                File.WriteAllText(Path.Combine(tempDir, MarkerFileName), version);
                if (Directory.Exists(targetDir))
                    Directory.Delete(targetDir, true);
                Directory.Move(tempDir, targetDir);
                return;
            }
            catch (Exception exception) when (IsTransient(exception))
            {
                DeleteIfExists(tempDir);
                if (attempt >= MaxAttempts)
                    throw new HttpRequestException(
                        $"Failed to download test fixtures from {url} after {MaxAttempts} attempts.",
                        exception,
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

    private static void DownloadAndExtract(HttpClient httpClient, string url, string tempDir)
    {
        Directory.CreateDirectory(tempDir);

        using HttpRequestMessage request = new(HttpMethod.Get, url);
        using HttpResponseMessage response = httpClient.Send(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        using Stream contentStream = response.Content.ReadAsStream();
        using GZipStream gzStream = new(contentStream, CompressionMode.Decompress);

        TarFile.ExtractToDirectory(gzStream, tempDir, overwriteFiles: true);
    }

    private static bool IsTransient(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: HttpStatusCode status } => IsTransient(status),
        TaskCanceledException or TimeoutException => true,
        IOException or InvalidDataException => true,
        _ => false
    };

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    private static void DeleteIfExists(string directory)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }
}
