using System.Collections.Generic;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using FluentAssertions;
using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class PragueStateTests : GeneralStateTestBase
{
    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(GeneralStateTest test) => (await RunTest(test)).Pass.Should().BeTrue();

    private static IEnumerable<GeneralStateTest> LoadTests()
    {
        TestsSourceLoader loader = new(new LoadPyspecTestsStrategy(), $"fixtures/state_tests/prague");
        return loader.LoadTests<GeneralStateTest>();
    }
}
