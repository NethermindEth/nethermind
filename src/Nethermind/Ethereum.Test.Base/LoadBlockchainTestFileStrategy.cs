// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Ethereum.Test.Base.Interfaces;

namespace Ethereum.Test.Base
{
    public class LoadBlockchainTestFileStrategy : ITestLoadStrategy
    {
        public IEnumerable<EthereumTest> Load(string testName, string? wildcard = null)
        {
            FileTestsSource fileTestsSource = new(testName, wildcard);
            return fileTestsSource.LoadTests(TestType.Blockchain);
        }
    }
}
