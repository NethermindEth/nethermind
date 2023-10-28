// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using Ethereum.Test.Base.Interfaces;
using SharpCompress.Archives;
using SharpCompress.Archives.Tar;

namespace Ethereum.Test.Base
{
    public class LoadPyspecTestsStrategy : ITestLoadStrategy
    {
        public IEnumerable<IEthereumTest> Load(string archiveAddress, string wildcard = null)
        {
            string[] addressAndDir = archiveAddress.Split("||");
            string testsDirectoryName = $"{AppContext.BaseDirectory}pyTests/";
            HttpClient httpClient = new();
            HttpResponseMessage response = httpClient.GetAsync(addressAndDir[0]).Result;
            response.EnsureSuccessStatusCode();
            // fetch filename from response
            string filename = response.Content.Headers.ContentDisposition.FileName;
            using Stream contentStream = response.Content.ReadAsStreamAsync().Result;
            // unarchive tar.gz file using sharpCompress
            using TarArchive archive = TarArchive.Open(contentStream);
            archive.ExtractToDirectory(testsDirectoryName);
            IEnumerable<string> testDirs;
            if (addressAndDir.Length > 1)
            {
                testDirs = Directory.EnumerateDirectories(testsDirectoryName + "/" + addressAndDir[1], "*", new EnumerationOptions { RecurseSubdirectories = true });
            }
            else
            {
                testDirs = Directory.EnumerateDirectories(testsDirectoryName, "*", new EnumerationOptions { RecurseSubdirectories = true });
            }
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
}
