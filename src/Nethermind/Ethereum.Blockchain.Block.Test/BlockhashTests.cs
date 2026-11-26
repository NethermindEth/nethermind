// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Block.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class BlockhashTests : GeneralStateTestBase
{
    [TestCaseSource(nameof(LoadTests))]
    public Task Test(GeneralStateTest test) => RunTestAndAssertPass(test);

    [Test]
    [NonParallelizable]
    public void LoadTests_discovers_every_fork_of_the_fixture()
    {
        string chunk = Environment.GetEnvironmentVariable("TEST_CHUNK");
        Environment.SetEnvironmentVariable("TEST_CHUNK", null);
        try
        {
            List<GeneralStateTest> tests = [.. LoadTests()];

            using (Assert.EnterMultipleScope())
            {
                Assert.That(tests.Select(static t => t.LoadFailure), Is.All.Null);
                Assert.That(tests.Select(static t => t.ForkName), Is.EquivalentTo(new[] { "London", "Berlin", "Istanbul" }));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_CHUNK", chunk);
        }
    }

    public static IEnumerable<GeneralStateTest> LoadTests()
    {
        TestsSourceLoader loader = new(new LoadGeneralStateTestsStrategy(), Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Blockhash"));
        return loader.LoadTests<GeneralStateTest>();
    }
}
