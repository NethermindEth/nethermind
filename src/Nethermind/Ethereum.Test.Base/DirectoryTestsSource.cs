/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ethereum.Test.Base
{
    public class DirectoryTestsSource : IBlockchainTestsSource
    {
        private readonly string _directory;
        private readonly string _wildcard;

        public DirectoryTestsSource(string directory, string wildcard = null)
        {
            _directory = directory ?? throw new ArgumentNullException(nameof(directory));
            _wildcard = wildcard;
        }

        public IEnumerable<BlockchainTest> LoadTests()
        {
            IEnumerable<string> testDirs;
            if (!Path.IsPathRooted(_directory))
            {
                Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
                testDirs = Directory.EnumerateDirectories(".", _directory);
            }
            else
            {
                testDirs = new[] {_directory};
            }

            if (Directory.Exists(".\\Tests\\"))
            {
                testDirs = testDirs.Union(Directory.EnumerateDirectories(".\\Tests\\", _directory));
            }

            List<BlockchainTest> testJsons = new List<BlockchainTest>();
            foreach (string testDir in testDirs)
            {
                testJsons.AddRange(LoadTestsFromDirectory(testDir, _wildcard));
            }

            return testJsons;
        }
        
        public IEnumerable<LegacyBlockchainTest> LoadLegacyTests()
        {
            IEnumerable<string> testDirs;
            if (!Path.IsPathRooted(_directory))
            {
                Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
                testDirs = Directory.EnumerateDirectories(".", _directory);
            }
            else
            {
                testDirs = new[] {_directory};
            }

            if (Directory.Exists(".\\Tests\\"))
            {
                testDirs = testDirs.Union(Directory.EnumerateDirectories(".\\Tests\\", _directory));
            }

            List<LegacyBlockchainTest> testJsons = new List<LegacyBlockchainTest>();
            foreach (string testDir in testDirs)
            {
                testJsons.AddRange(LoadLegacyTestsFromDirectory(testDir, _wildcard));
            }

            return testJsons;
        }

        private static IEnumerable<BlockchainTest> LoadTestsFromDirectory(string testDir, string wildcard)
        {
            List<BlockchainTest> testsByName = new List<BlockchainTest>();
            List<string> testFiles = Directory.EnumerateFiles(testDir).ToList();
            foreach (string testFile in testFiles)
            {
                FileTestsSource fileTestsSource = new FileTestsSource(testFile, wildcard);
                try
                {
                    var tests = fileTestsSource.LoadTests().ToList();
                    foreach (BlockchainTest blockchainTest in tests)
                    {
                        blockchainTest.Category = testDir;
                    }

                    testsByName.AddRange(tests);
                }
                catch (Exception e)
                {
                    testsByName.Add(new BlockchainTest {Name = testFile, LoadFailure = $"Failed to load: {e.Message}"});
                }
            }

            return testsByName;
        }
        
        private static IEnumerable<LegacyBlockchainTest> LoadLegacyTestsFromDirectory(string testDir, string wildcard)
        {
            List<LegacyBlockchainTest> testsByName = new List<LegacyBlockchainTest>();
            List<string> testFiles = Directory.EnumerateFiles(testDir).ToList();
            foreach (string testFile in testFiles)
            {
                FileTestsSource fileTestsSource = new FileTestsSource(testFile, wildcard);
                try
                {
                    var tests = fileTestsSource.LoadLegacyTests().ToList();
                    foreach (LegacyBlockchainTest blockchainTest in tests)
                    {
                        blockchainTest.Category = testDir;
                    }

                    testsByName.AddRange(tests);
                }
                catch (Exception e)
                {
                    testsByName.Add(new LegacyBlockchainTest {Name = testFile, LoadFailure = $"Failed to load: {e.Message}"});
                }
            }

            return testsByName;
        }
    }
}