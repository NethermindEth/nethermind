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
    public IEnumerable<IEthereumTest> Load(string testsDir, string wildcard = null)
    {
        testsDir = GetArchiveVersionAndName(testsDir, out string archiveVersion, out string archiveName);
        string testsDirectoryName = Path.Combine(AppContext.BaseDirectory, "PyTests", archiveVersion, archiveName.Split('.')[0]);
        if (!Directory.Exists(testsDirectoryName)) // Prevent redownloading the fixtures if they already exists with this version and archive name
            DownloadAndExtract(archiveVersion, archiveName, testsDirectoryName);
        IEnumerable<string> testDirs = !string.IsNullOrEmpty(testsDir)
            ? Directory.EnumerateDirectories(Path.Combine(testsDirectoryName, testsDir), "*", new EnumerationOptions { RecurseSubdirectories = true })
            : Directory.EnumerateDirectories(testsDirectoryName, "*", new EnumerationOptions { RecurseSubdirectories = true });
        return testDirs.SelectMany(td => LoadTestsFromDirectory(td, wildcard)).ToList();
    }

    private string GetArchiveVersionAndName(string testsDir, out string archiveVersion, out string archiveName)
    {
        string[] versionAndDir = testsDir.Split(",");
        archiveVersion = Constants.DEFAULT_ARCHIVE_VERSION;
        archiveName = Constants.DEFAULT_ARCHIVE_NAME;
        if (versionAndDir.Length < 3)
        {
            throw new ArgumentException("Invalid testsDir argument. It should be in format: <version>,<archiveName>,<testsDir>");
        }
        if (!string.IsNullOrEmpty(versionAndDir[0]))
            archiveVersion = versionAndDir[0];
        if (!string.IsNullOrEmpty(versionAndDir[1]))
            archiveName = versionAndDir[1];
        if (!string.IsNullOrEmpty(versionAndDir[2]))
            testsDir = versionAndDir[2];
        return testsDir;
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
