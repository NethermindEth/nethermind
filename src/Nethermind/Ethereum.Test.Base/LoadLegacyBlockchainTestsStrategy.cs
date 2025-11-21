// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Ethereum.Test.Base.Interfaces;

namespace Ethereum.Test.Base
{
    public class LoadLegacyBlockchainTestsStrategy : ITestLoadStrategy
    {
        public IEnumerable<EthereumTest> Load(string testsDirectoryName, string wildcard = null)
        {
            IEnumerable<string> testDirs;
            if (!Path.IsPathRooted(testsDirectoryName))
            {
                string legacyTestsDirectory = GetLegacyBlockchainTestsDirectory();

                testDirs = Directory.EnumerateDirectories(legacyTestsDirectory, testsDirectoryName, new EnumerationOptions { RecurseSubdirectories = true });
            }
            else
            {
                testDirs = new[] { testsDirectoryName };
            }

            List<EthereumTest> testJsons = new();
            foreach (string testDir in testDirs)
            {
                testJsons.AddRange(LoadTestsFromDirectory(testDir, wildcard));
            }

            return testJsons;
        }

        private static string GetLegacyBlockchainTestsDirectory()
        {
            string currentDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string rootDirectory = currentDirectory.Remove(currentDirectory.LastIndexOf("src"));

            return Path.Combine(rootDirectory, "src", "tests", "LegacyTests", "Cancun", "BlockchainTests");
        }

        private static IEnumerable<EthereumTest> LoadTestsFromDirectory(string testDir, string wildcard)
        {
            List<EthereumTest> testsByName = new();
            IEnumerable<string> testFiles = Directory.EnumerateFiles(testDir);

            foreach (string testFile in testFiles)
            {
                FileTestsSource fileTestsSource = new(testFile, wildcard);
                try
                {
                    var tests = fileTestsSource.LoadTests(TestType.Blockchain);
                    foreach (EthereumTest blockchainTest in tests)
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
