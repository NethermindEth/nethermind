//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Collections.Generic;
using System.IO;
using Ethereum.Test.Base.Interfaces;

namespace Ethereum.Test.Base
{
    public class LoadTestsFromLocalFile : ITestLoadStrategy
    {
        public IEnumerable<IEthereumTest> Load(string testDirectoryName, string wildcard = null)
        {
            List<GeneralStateTest> testsByName = new();
            IEnumerable<string> testFiles = Directory.EnumerateFiles(testDirectoryName);

            foreach (string testFile in testFiles)
            {
                FileTestsSource fileTestsSource = new(testFile, wildcard);
                try
                {
                    var tests = fileTestsSource.LoadGeneralStateTests();
                    foreach (GeneralStateTest blockchainTest in tests)
                    {
                        blockchainTest.Category = testDirectoryName;
                    }

                    testsByName.AddRange(tests);
                }
                catch (Exception e)
                {
                    testsByName.Add(new GeneralStateTest {Name = testFile, LoadFailure = $"Failed to load: {e}"});
                }
            }

            return testsByName;
        }
    }
}
