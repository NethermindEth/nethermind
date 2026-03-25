// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;

namespace Ethereum.Test.Base
{
    public class TestsSourceLoader(ITestLoadStrategy testLoadStrategy, string path, string? wildcard = null)
        : ITestSourceLoader
    {
        private readonly ITestLoadStrategy _testLoadStrategy = testLoadStrategy ?? throw new ArgumentNullException(nameof(testLoadStrategy));
        private readonly string _path = path ?? throw new ArgumentNullException(nameof(path));

        public IEnumerable<EthereumTest> LoadTests() =>
            TestChunkFilter.FilterByChunk(_testLoadStrategy.Load(_path, wildcard));

        public IEnumerable<TTestType> LoadTests<TTestType>() where TTestType : EthereumTest =>
            TestChunkFilter.FilterByChunk(_testLoadStrategy.Load(_path, wildcard).OfType<TTestType>());
    }
}
