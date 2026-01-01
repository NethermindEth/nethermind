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
using Ethereum.Test.Base.Interfaces;

namespace Ethereum.Blockchain.Pyspec.Test;

public class LoadPyspecTestsStrategy : ITestLoadStrategy
{
    private static readonly object _downloadLock = new();

    public string ArchiveVersion { get; init; } = Constants.DEFAULT_ARCHIVE_VERSION;
    public string ArchiveName { get; init; } = Constants.DEFAULT_ARCHIVE_NAME;

    public IEnumerable<EthereumTest> Load(string testsDir, string wildcard = null)
    {
        string testsDirectoryName = GetTestsDirectoryName();
        EnsureFixturesAvailable(ArchiveVersion, ArchiveName, testsDirectoryName);

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
        return testDirs.SelectMany(td => LoadTestsFromDirectory(td, wildcard, testType));
    }

    private string GetTestsDirectoryName()
    {
        // Cache location can be overridden for CI / offline setups.
        // Example: export NETHERMIND_PYSPEC_CACHE_DIR=/mnt/ssd/nethermind-cache
        string cacheRootOverride = Environment.GetEnvironmentVariable("NETHERMIND_PYSPEC_CACHE_DIR") ?? string.Empty;
        string cacheRoot = !string.IsNullOrWhiteSpace(cacheRootOverride)
            ? cacheRootOverride
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nethermind");

        return Path.Combine(cacheRoot, "PyTests", ArchiveVersion, ArchiveName.Split('.')[0]);
    }

    private static void EnsureFixturesAvailable(string archiveVersion, string archiveName, string testsDirectoryName)
    {
        // Prevent redownloading/extracting for each TestCaseSource call and avoid parallel download races.
        string markerFile = Path.Combine(testsDirectoryName, ".nethermind_pyspec_extracted");
        if (File.Exists(markerFile))
        {
            return;
        }

        lock (_downloadLock)
        {
            if (File.Exists(markerFile))
            {
                return;
            }

            DownloadAndExtract(archiveVersion, archiveName, testsDirectoryName, markerFile);
        }
    }

    private static void DownloadAndExtract(string archiveVersion, string archiveName, string testsDirectoryName, string markerFile)
    {
        string url = string.Format(Constants.ARCHIVE_URL_TEMPLATE, archiveVersion, archiveName);

        int timeoutSeconds = 1800;
        string timeoutOverride = Environment.GetEnvironmentVariable("NETHERMIND_PYSPEC_HTTP_TIMEOUT_SECONDS") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(timeoutOverride) && int.TryParse(timeoutOverride, out int parsed) && parsed > 0)
        {
            timeoutSeconds = parsed;
        }

        Console.WriteLine($"[Pyspec] Fetching fixtures: {url}");
        Console.WriteLine($"[Pyspec] Cache dir: {testsDirectoryName}");
        Console.WriteLine($"[Pyspec] HTTP timeout: {timeoutSeconds}s (override with NETHERMIND_PYSPEC_HTTP_TIMEOUT_SECONDS)");

        string parentDir = Path.GetDirectoryName(testsDirectoryName) ?? testsDirectoryName;
        Directory.CreateDirectory(parentDir);

        // Download to a file first (more robust than streaming directly into TarFile).
        string archivePath = Path.Combine(parentDir, archiveName);

        // Clean up any previous partial extraction.
        if (Directory.Exists(testsDirectoryName))
        {
            try
            {
                Directory.Delete(testsDirectoryName, recursive: true);
            }
            catch
            {
                // Best-effort; extraction will fail if directory is in bad state.
            }
        }

        using (HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(timeoutSeconds) })
        using (HttpResponseMessage response = httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
        {
            response.EnsureSuccessStatusCode();
            using Stream contentStream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            using FileStream fileStream = new(archivePath, FileMode.Create, FileAccess.Write, FileShare.None);
            contentStream.CopyTo(fileStream);
        }

        Directory.CreateDirectory(testsDirectoryName);
        using (FileStream fileStream = new(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (GZipStream gzStream = new(fileStream, CompressionMode.Decompress))
        {
            TarFile.ExtractToDirectory(gzStream, testsDirectoryName, overwriteFiles: true);
        }

        File.WriteAllText(markerFile, $"{DateTimeOffset.UtcNow:O}\n{url}\n");
    }

    private IEnumerable<EthereumTest> LoadTestsFromDirectory(string testDir, string wildcard, TestType testType)
    {
        List<EthereumTest> testsByName = new();
        IEnumerable<string> testFiles = Directory.EnumerateFiles(testDir);

        foreach (string testFile in testFiles)
        {
            FileTestsSource fileTestsSource = new(testFile, wildcard);

            IEnumerable<EthereumTest> tests = fileTestsSource.LoadTests(testType);

            foreach (EthereumTest test in tests)
            {
                test.Category ??= testDir;
            }
            testsByName.AddRange(tests);
        }

        return testsByName;
    }
}
