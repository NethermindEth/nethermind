// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using Ethereum.Test.Base;
using Ethereum.Test.Base.Interfaces;

namespace Ethereum.Blockchain.Pyspec.Test;

public class LoadPyspecTestsStrategy : ITestLoadStrategy
{
    public string ArchiveVersion { get; init; } = Constants.DEFAULT_ARCHIVE_VERSION;
    public string ArchiveName { get; init; } = Constants.DEFAULT_ARCHIVE_NAME;

    public IEnumerable<EthereumTest> Load(string testsDir, string wildcard = null)
    {
        string testsDirectoryName = Path.Combine(GetFixturesCacheDirectory(), "PyTests", ArchiveVersion, ArchiveName.Split('.')[0]);
        if (!Directory.Exists(testsDirectoryName)) // Prevent redownloading the fixtures if they already exists with this version and archive name
            DownloadAndExtract(ArchiveVersion, ArchiveName, testsDirectoryName);

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

    /// <summary>
    /// Returns a stable cache directory for EEST fixtures that survives dotnet rebuilds.
    /// Walks up from <see cref="AppContext.BaseDirectory"/> (artifacts/bin/Project/config/)
    /// to the artifacts directory and uses an <c>eest</c> subfolder instead of <c>bin</c>.
    /// Falls back to <see cref="AppContext.BaseDirectory"/> if the expected layout is not found.
    /// </summary>
    private static string GetFixturesCacheDirectory()
    {
        // AppContext.BaseDirectory is typically: .../artifacts/bin/Ethereum.Blockchain.Pyspec.Test/release/
        // We want:                              .../artifacts/eest/
        DirectoryInfo dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.Parent != null && string.Equals(dir.Name, "bin", StringComparison.OrdinalIgnoreCase)
                && string.Equals(dir.Parent.Name, "artifacts", StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(dir.Parent.FullName, "eest");
            }
            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
    }

    private void DownloadAndExtract(string archiveVersion, string archiveName, string testsDirectoryName)
    {
        using HttpClient httpClient = new();
        HttpResponseMessage response = httpClient.GetAsync(string.Format(Constants.ARCHIVE_URL_TEMPLATE, archiveVersion, archiveName)).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        using Stream contentStream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        using GZipStream gzStream = new(contentStream, CompressionMode.Decompress);

        if (!Directory.Exists(testsDirectoryName))
            Directory.CreateDirectory(testsDirectoryName);

        TarFile.ExtractToDirectory(gzStream, testsDirectoryName, true);
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
