// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using Ethereum.Test.Base;

namespace Ethereum.Blockchain.Pyspec.Test;

public class LoadPyspecTestsStrategy : ITestLoadStrategy
{
    public string ArchiveVersion { get; init; } = Constants.DEFAULT_ARCHIVE_VERSION;
    public string ArchiveName { get; init; } = Constants.DEFAULT_ARCHIVE_NAME;

    public IEnumerable<EthereumTest> Load(string testsDir, string wildcard = null)
    {
        string testsDirectoryName = Path.Combine(GetFixturesCacheDirectory(), "PyTests", ArchiveVersion, ArchiveName.Split('.')[0]);
        EnsureFixturesDownloaded(testsDirectoryName, ArchiveVersion, ArchiveName);

        TestType testType = TestType.Blockchain;
        foreach (TestType type in Enum.GetValues<TestType>())
        {
            if (testsDir.Contains($"{type}_tests", StringComparison.OrdinalIgnoreCase))
            {
                testType = type;
                break;
            }
        }

        IEnumerable<string> testDirs = !string.IsNullOrEmpty(testsDir)
            ? Directory.EnumerateDirectories(Path.Combine(testsDirectoryName, testsDir), "*", new EnumerationOptions { RecurseSubdirectories = true })
            : Directory.EnumerateDirectories(testsDirectoryName, "*", new EnumerationOptions { RecurseSubdirectories = true });
        return testDirs.SelectMany(td => TestLoadStrategy.LoadTestsFromDirectory(td, wildcard, testType));
    }

    /// <summary>
    /// Returns a stable cache directory for EEST fixtures that survives dotnet rebuilds.
    /// Uses the system temp directory with a stable subfolder name.
    /// </summary>
    private static string GetFixturesCacheDirectory() =>
        Path.Combine(Path.GetTempPath(), "nethermind-eest");

    private static readonly string s_completedMarker = ".completed";

    /// <summary>
    /// Thread- and process-safe fixture download. Uses a named mutex so that concurrent
    /// test processes sharing the same cache directory do not race on download/extract.
    /// A <c>.completed</c> marker file is written after a successful extraction so that
    /// a partial extraction from a previous crash is detected and retried.
    /// </summary>
    private static void EnsureFixturesDownloaded(string testsDirectoryName, string archiveVersion, string archiveName)
    {
        string markerPath = Path.Combine(testsDirectoryName, s_completedMarker);
        if (File.Exists(markerPath))
        {
            Console.WriteLine($"EEST fixtures already cached at {testsDirectoryName}");
            return;
        }

        // Named mutex keyed by target path to synchronize across processes.
        string mutexName = $"Global\\eest_{archiveVersion}_{archiveName}".Replace('/', '_').Replace('\\', '_');
        using Mutex mutex = new(false, mutexName);
        Console.WriteLine($"Waiting for EEST fixture lock ({archiveName} {archiveVersion})...");
        mutex.WaitOne();
        try
        {
            // Re-check after acquiring the mutex — another process may have finished.
            if (File.Exists(markerPath))
            {
                Console.WriteLine("EEST fixtures were downloaded by another process.");
                return;
            }

            Console.WriteLine($"Downloading EEST fixtures ({archiveName} {archiveVersion})...");
            DownloadAndExtract(archiveVersion, archiveName, testsDirectoryName);
            File.WriteAllText(markerPath, archiveVersion);
            Console.WriteLine($"EEST fixtures extracted to {testsDirectoryName}");
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static void DownloadAndExtract(string archiveVersion, string archiveName, string testsDirectoryName)
    {
        // Clean up any partial extraction from a previous interrupted attempt.
        if (Directory.Exists(testsDirectoryName))
            Directory.Delete(testsDirectoryName, true);

        Directory.CreateDirectory(testsDirectoryName);

        using HttpClient httpClient = new();
        // Stream directly from network → GZip → Tar to avoid buffering the entire archive in memory.
        using HttpRequestMessage request = new(HttpMethod.Get,
            string.Format(Constants.ARCHIVE_URL_TEMPLATE, archiveVersion, archiveName));
        using HttpResponseMessage response = httpClient.Send(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        using Stream contentStream = response.Content.ReadAsStream();
        using GZipStream gzStream = new(contentStream, CompressionMode.Decompress);

        TarFile.ExtractToDirectory(gzStream, testsDirectoryName, overwriteFiles: true);
    }
}
