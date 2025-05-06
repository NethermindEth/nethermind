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
            return _testLoadStrategy.Load(_path, _wildcard);
        }
        public IEnumerable<TTestType> LoadTests<TTestType>()
            where TTestType : EthereumTest
        {
            return _testLoadStrategy.Load(_path, _wildcard).Cast<TTestType>();
        }
    }
}
