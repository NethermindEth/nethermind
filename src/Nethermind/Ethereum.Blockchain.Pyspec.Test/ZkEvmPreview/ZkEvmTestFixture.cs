// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Test.ZkEvm;

/// <summary>
/// Generic base for zkEVM blockchain tests.
/// Wildcard is read from <see cref="EipWildcardAttribute"/> on <typeparamref name="TSelf"/>.
/// In CI, only runs on Linux x64 to stay within the job timeout budget.
/// </summary>
[Parallelizable(ParallelScope.All)]
public abstract class ZkEvmBlockChainTestFixture<TSelf> : BlockchainTestBase
{
    [SetUp]
    public void SkipInCiOnSlowRunners() => CiRunnerGuard.SkipIfNotLinuxX64();

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => Assert.That((await RunTest(test)).Pass, Is.True);

    private static IEnumerable<BlockchainTest> LoadTests() =>
        new TestsSourceLoader(
            new LoadPyspecTestsStrategy
            {
                ArchiveName = "fixtures_zkevm.tar.gz",
                ArchiveVersion = "zkevm@v0.3.3"
            },
            "fixtures/blockchain_tests/for_amsterdam",
            typeof(TSelf).GetCustomAttribute<EipWildcardAttribute>().Wildcard
        )
        .LoadTests<BlockchainTest>();
}
