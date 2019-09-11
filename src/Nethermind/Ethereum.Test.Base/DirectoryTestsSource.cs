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
using Newtonsoft.Json;

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
//            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
//            IEnumerable<string> testDirs = Directory.EnumerateDirectories(".", _directory);
            IEnumerable<string> testDirs = Directory.EnumerateDirectories(AppDomain.CurrentDomain.BaseDirectory, _directory);
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

        private static IEnumerable<BlockchainTest> LoadTestsFromDirectory(string testDir, string wildcard)
        {
            List<BlockchainTest> testsByName = new List<BlockchainTest>();
            List<string> testFiles = Directory.EnumerateFiles(testDir).ToList();
            foreach (string testFile in testFiles)
            {
                FileTestsSource fileTestsSource = new FileTestsSource(testFile, wildcard);
                try
                {
                    testsByName.AddRange(fileTestsSource.LoadTests());
                }
                catch (Exception e)
                {
                    testsByName.Add(new BlockchainTest {Name = testFile, LoadFailure = $"Failed to load: {e.Message}"});
                }
            }

            return testsByName;
        }
    }
}