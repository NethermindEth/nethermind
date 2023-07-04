// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Ethereum.Test.Base.Interfaces;

namespace Ethereum.Test.Base
{
    public class LoadBlockchainTestsStrategy : ITestLoadStrategy
    {
        public IEnumerable<IEthereumTest> Load(string testsDirectoryName, string wildcard = null)
        {
            IEnumerable<string> testDirs;
            if (!Path.IsPathRooted(testsDirectoryName))
            {
                string testDirectory = GetBlockchainTestsDirectory();

                testDirs = Directory.EnumerateDirectories(testDirectory, testsDirectoryName, new EnumerationOptions { RecurseSubdirectories = true });
            }
            else
            {
                testDirs = new[] { testsDirectoryName };
            }

            List<BlockchainTest> testJsons = new();
            foreach (string testDir in testDirs)
            {
                testJsons.AddRange(LoadTestsFromDirectory(testDir, wildcard));
            }

            return testJsons;
        }

        private string GetBlockchainTestsDirectory()
        {
            char pathSeparator = Path.AltDirectorySeparatorChar;
            string currentDirectory = AppDomain.CurrentDomain.BaseDirectory;

            return currentDirectory.Remove(currentDirectory.LastIndexOf("src")) + $"src{pathSeparator}tests{pathSeparator}BlockchainTests";
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
