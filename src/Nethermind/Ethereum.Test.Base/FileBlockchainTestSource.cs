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
    public class FileBlockchainTestSource : IBlockchainTestSource
    {
        private readonly string _directory;
        private readonly string _wildcard;

        public FileBlockchainTestSource(string directory, string wildcard = null)
        {
            _directory = directory ?? throw new ArgumentNullException(nameof(directory));
            _wildcard = wildcard;
        }

        public IEnumerable<BlockchainTest> LoadTests()
        {
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
            IEnumerable<string> testDirs = Directory.EnumerateDirectories(".", _directory);
            if (Directory.Exists(".\\Tests\\"))
            {
                testDirs = testDirs.Union(Directory.EnumerateDirectories(".\\Tests\\", _directory));
            }

            Dictionary<string, Dictionary<string, BlockchainTestJson>> testJsons = new Dictionary<string, Dictionary<string, BlockchainTestJson>>();
            foreach (string testDir in testDirs)
            {
                testJsons[testDir] = LoadTestsFromDirectory(testDir, _wildcard);
            }

            return testJsons.SelectMany(d => d.Value).Select(pair => JsonToBlockchainTest.Convert(pair.Key, pair.Value));
        }

        private static Dictionary<string, BlockchainTestJson> LoadTestsFromDirectory(string testDir, string wildcard)
        {
            Dictionary<string, BlockchainTestJson> testsByName = new Dictionary<string, BlockchainTestJson>();
            List<string> testFiles = Directory.EnumerateFiles(testDir).ToList();
            foreach (string testFile in testFiles)
            {
                try
                {
                    if (Path.GetFileName(testFile).StartsWith("."))
                    {
                        continue;
                    }

                    if (wildcard != null && !testFile.Contains(wildcard))
                    {
                        continue;
                    }

                    string json = File.ReadAllText(testFile);
                    Dictionary<string, BlockchainTestJson> testsInFile = JsonConvert.DeserializeObject<Dictionary<string, BlockchainTestJson>>(json);
                    foreach (KeyValuePair<string, BlockchainTestJson> namedTest in testsInFile)
                    {
                        string[] transitionInfo = namedTest.Value.Network.Split("At");
                        string[] networks = transitionInfo[0].Split("To");
                        for (int i = 0; i < networks.Length; i++)
                        {
                            networks[i] = networks[i].Replace("EIP150", "TangerineWhistle");
                            networks[i] = networks[i].Replace("EIP158", "SpuriousDragon");
                            networks[i] = networks[i].Replace("DAO", "Dao");
                        }

                        namedTest.Value.EthereumNetwork = JsonToBlockchainTest.ParseSpec(networks[0]);
                        if (transitionInfo.Length > 1)
                        {
                            namedTest.Value.TransitionBlockNumber = int.Parse(transitionInfo[1]);
                            namedTest.Value.EthereumNetworkAfterTransition = JsonToBlockchainTest.ParseSpec(networks[1]);
                        }

                        testsByName.Add(namedTest.Key, namedTest.Value);
                    }
                }
                catch (Exception e)
                {
                    testsByName.Add(testFile, new BlockchainTestJson {LoadFailure = $"Failed to load: {e.Message}"});
                }
            }

            return testsByName;
        }
    }
}