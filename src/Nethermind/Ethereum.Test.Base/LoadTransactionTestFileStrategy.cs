// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Ethereum.Test.Base
{
    public class LoadTransactionTestFileStrategy : ITestLoadStrategy
    {
        public IEnumerable<EthereumTest> Load(string testName, string? wildcard = null)
        {
            FileTestsSource fileTestsSource = new(testName, wildcard);
            return fileTestsSource.LoadTests(TestType.Transaction);
        }
    }
}
