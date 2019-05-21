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
using System.Threading.Tasks;
using Ethereum.Test.Base;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Runner
{
    public class BugHunter : BlockchainTestBase, ITestInRunner
    {
        public async Task<CategoryResult> RunTests(string subset, string testWildcard, int iterations = 1)
        {
            ConsoleColor defaultColor = Console.ForegroundColor;
            List<string> failingTests = new List<string>();

            string directoryName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "FailingTests");
            IEnumerable<BlockchainTest> tests = LoadTests(subset);
            foreach (BlockchainTest test in tests)
            {
                if (testWildcard != null && !test.Name.Contains(testWildcard))
                {
                    continue;
                }

                Setup(null);

                try
                {
                    Console.Write($"{test.Name,-80} ");
                    Assert.IsNull(test.LoadFailure);

                    await RunTest(test);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("PASS");
                    Console.ForegroundColor = defaultColor;
                }
                catch (Exception)
                {
                    failingTests.Add(test.Name);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("FAIL");
                    Console.ForegroundColor = defaultColor;
                    NLogManager manager = new NLogManager(string.Concat(subset, "_", test.Name, ".txt"), directoryName);
                    try
                    {
                        if (!Directory.Exists(directoryName))
                        {
                            Directory.CreateDirectory(directoryName);
                        }

                        Setup(manager);
                        await RunTest(test);
                    }
                    catch (Exception againEx)
                    {
                        manager.GetClassLogger().Error(againEx.ToString());
                    }
                }
            }

            return new CategoryResult(0, failingTests.ToArray());
        }
    }
}