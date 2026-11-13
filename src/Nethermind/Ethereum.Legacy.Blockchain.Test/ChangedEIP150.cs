// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Legacy.Blockchain.Test;

/// <summary>Runs <c>stChangedEIP150</c>, which only the Constantinople set of the legacy tests has.</summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class ChangedEIP150 : GeneralStateTestBase
{
    [TestCaseSource(nameof(LoadTests))]
    public Task Test(GeneralStateTest test) => RunTestAndAssertPass(test);

    public static IEnumerable<GeneralStateTest> LoadTests() =>
        new TestsSourceLoader(new LoadLegacyGeneralStateTestsStrategy(LegacyTestSet.Constantinople), "stChangedEIP150")
            .LoadTests<GeneralStateTest>();
}
