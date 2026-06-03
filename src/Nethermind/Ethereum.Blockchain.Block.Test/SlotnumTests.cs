// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Block.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class SlotnumTests : GeneralStateTestBase
{
    [TestCaseSource(nameof(LoadTests))]
    public void Test(GeneralStateTest test) => Assert.That(RunTest(test).Pass, Is.True);

    public static IEnumerable<GeneralStateTest> LoadTests()
    {
        TestsSourceLoader loader = new(new LoadGeneralStateTestsStrategy(), Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Slotnum"));
        return loader.LoadTests<GeneralStateTest>();
    }
}
