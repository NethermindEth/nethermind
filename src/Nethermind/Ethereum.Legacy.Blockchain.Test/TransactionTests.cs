// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Ethereum.Test.Base;
using Ethereum.Test.Base.Interfaces;
using NUnit.Framework;

namespace Ethereum.Legacy.Blockchain.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class TransactionTests : GeneralStateTestBase
{
    [TestCaseSource(nameof(LoadTests))]
    public void Test(GeneralStateTest test)
    {
        Assert.That(RunTest(test).Pass, Is.True);
    }

    public static IEnumerable<GeneralStateTest> LoadTests()
    {
        try
        {
            var loader = new TestsSourceLoader(new LoadLegacyGeneralStateTestsStrategy(), "stTransactionTest");
            // We don't handle invalid transaction's RLP cases, that are loaded as FailedToLoadTest
            return loader.LoadTests<EthereumTest>().OfType<GeneralStateTest>();
        }
        catch
        {
            throw;
        }
    }
}

