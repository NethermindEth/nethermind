// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
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
        string[] versionAndDir = testsDir.Split(",");
        string archiveVersion = Constants.DEFAULT_ARCHIVE_VERSION;
        string archiveName = Constants.DEFAULT_ARCHIVE_NAME;
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
        string testsDirectoryName = $"{AppContext.BaseDirectory}PyTests/{archiveVersion}/{archiveName.Split('.')[0]}";
        if (!Directory.Exists(testsDirectoryName))
        {
            HttpClient httpClient = new();
            HttpResponseMessage response = httpClient.GetAsync(string.Format(Constants.ARCHIVE_URL_TEMPLATE, archiveVersion, archiveName)).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            using Stream contentStream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            using TarArchive archive = TarArchive.Open(contentStream);
            archive.ExtractToDirectory(testsDirectoryName);
        }
        IEnumerable<string> testDirs = !string.IsNullOrEmpty(testsDir)
            ? Directory.EnumerateDirectories(testsDirectoryName + "/" + testsDir, "*", new EnumerationOptions { RecurseSubdirectories = true })
            : Directory.EnumerateDirectories(testsDirectoryName, "*", new EnumerationOptions { RecurseSubdirectories = true });
        List<BlockchainTest> testJsons = new();
        foreach (string testDir in testDirs)
        {
            testJsons.AddRange(LoadTestsFromDirectory(testDir, wildcard));
        }
        return testJsons;
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
                var tests = fileTestsSource.LoadBlockchainTests();
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
