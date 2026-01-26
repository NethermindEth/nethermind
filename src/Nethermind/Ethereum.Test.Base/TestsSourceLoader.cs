// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;

using Ethereum.Test.Base.Interfaces;

namespace Ethereum.Test.Base
{
    public class TestsSourceLoader : ITestSourceLoader
    {
        private readonly ITestLoadStrategy _testLoadStrategy;
        private readonly string _path;
        private readonly string _wildcard;

        public TestsSourceLoader(ITestLoadStrategy testLoadStrategy, string path, string wildcard = null)
        {
            _testLoadStrategy = testLoadStrategy ?? throw new ArgumentNullException(nameof(testLoadStrategy));
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _wildcard = wildcard;
        }

        public IEnumerable<EthereumTest> LoadTests()
        {
            IEnumerable<EthereumTest> tests = _testLoadStrategy.Load(_path, _wildcard);
            return TestChunkFilter.FilterByChunk(tests);
        }

        public IEnumerable<TTestType> LoadTests<TTestType>()
            where TTestType : EthereumTest
        {
            // Use OfType instead of Cast to filter out FailedToLoadTest instances
            IEnumerable<TTestType> tests = _testLoadStrategy.Load(_path, _wildcard).OfType<TTestType>();
            return TestChunkFilter.FilterByChunk(tests);
        }
    }
}
