// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using Ethereum.Test.Base;
using Ethereum.Test.Base.Interfaces;
using SharpCompress.Archives;
using SharpCompress.Archives.Tar;

namespace Ethereum.Blockchain.Pyspec.Test;

public class LoadPyspecTestsStrategy : ITestLoadStrategy
{
    public string ArchiveVersion { get; init; } = Constants.DEFAULT_ARCHIVE_VERSION;
    public string ArchiveName { get; init; } = Constants.DEFAULT_ARCHIVE_NAME;

    public IEnumerable<IEthereumTest> Load(string testsDir, string wildcard = null)
    {
        string testsDirectoryName = Path.Combine(AppContext.BaseDirectory, "PyTests", ArchiveVersion, ArchiveName.Split('.')[0]);
        if (!Directory.Exists(testsDirectoryName)) // Prevent redownloading the fixtures if they already exists with this version and archive name
            DownloadAndExtract(ArchiveVersion, ArchiveName, testsDirectoryName);
        IEnumerable<string> testDirs = !string.IsNullOrEmpty(testsDir)
            ? Directory.EnumerateDirectories(Path.Combine(testsDirectoryName, testsDir), "*", new EnumerationOptions { RecurseSubdirectories = true })
            : Directory.EnumerateDirectories(testsDirectoryName, "*", new EnumerationOptions { RecurseSubdirectories = true });
        return testDirs.SelectMany(td => LoadTestsFromDirectory(td, wildcard));
    }

    private void DownloadAndExtract(string archiveVersion, string archiveName, string testsDirectoryName)
    {
        using HttpClient httpClient = new();
        HttpResponseMessage response = httpClient.GetAsync(string.Format(Constants.ARCHIVE_URL_TEMPLATE, archiveVersion, archiveName)).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        using Stream contentStream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        using TarArchive archive = TarArchive.Open(contentStream);
        archive.ExtractToDirectory(testsDirectoryName);
    }

    private IEnumerable<BlockchainTest> LoadTestsFromDirectory(string testDir, string wildcard)
    {
        List<BlockchainTest> testsByName = new();
        IEnumerable<string> testFiles = Directory.EnumerateFiles(testDir);

        foreach (string testFile in testFiles)
        {
            FileTestsSource fileTestsSource = new(testFile, wildcard);
            try
            {
                IEnumerable<BlockchainTest> tests = fileTestsSource.LoadBlockchainTests();
                foreach (BlockchainTest blockchainTest in tests)
                {
                    blockchainTest.Category = testDir;
                }

                testsByName.AddRange(tests);
            }
            catch (Exception e)
            {
                testsByName.Add(new BlockchainTest { Name = testFile, LoadFailure = $"Failed to load: {e}" });
            }
        }

        return testsByName;
    }
}
