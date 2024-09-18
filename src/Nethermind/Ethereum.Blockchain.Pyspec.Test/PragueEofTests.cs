// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class PragueEofTests : EofTestBase
{
    [TestCaseSource(nameof(LoadTests))]
    public void Test(EofTest test) => RunCITest(test);

    private static IEnumerable<TestCaseData> LoadTests()
    {
        TestsSourceLoader loader = new(new LoadPyspecTestsStrategy()
        {
            ArchiveName = "fixtures_eip7692.tar.gz",
            ArchiveVersion = "eip7692@v1.0.9"
        }, $"fixtures/eof_tests/prague");
        return loader.LoadTests().Cast<EofTest>().Select(t => new TestCaseData(t)
            .SetName(t.Name)
            .SetCategory(t.Category));
    }
}
