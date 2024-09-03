// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class PragueEofTests : EofTestBase
{
    [TestCaseSource(nameof(LoadTests))]
    public void Test(EofTest test) => RunTest(test);

    private static IEnumerable<EofTest> LoadTests()
    {
        TestsSourceLoader loader = new(new LoadPyspecTestsStrategy()
        {
            ArchiveName = "fixtures_eip7692.tar.gz",
            ArchiveVersion = "eip7692@v1.0.8"
        }, $"fixtures/eof_tests/prague");
        return loader.LoadTests().Cast<EofTest>();
    }
}