// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using FluentAssertions;
using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Test.Amsterdam;

/// <summary>
/// Generic base for Amsterdam EIP blockchain tests.
/// Wildcard is read from <see cref="EipWildcardAttribute"/> on <typeparamref name="TSelf"/>.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class AmsterdamBlockChainTestFixture<TSelf> : BlockchainTestBase
{
    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => await RunTest(test);

    public static IEnumerable<BlockchainTest> LoadTests() =>
        new TestsSourceLoader(new LoadPyspecTestsStrategy
        {
            ArchiveVersion = Constants.BalArchiveVersion,
            ArchiveName = Constants.BalArchiveName
        }, "fixtures/blockchain_tests", typeof(TSelf).GetCustomAttribute<EipWildcardAttribute>()!.Wildcard).LoadTests<BlockchainTest>();
}

/// <summary>
/// Generic base for Amsterdam EIP state tests.
/// Wildcard is read from <see cref="EipWildcardAttribute"/> on <typeparamref name="TSelf"/>.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class AmsterdamStateTestFixture<TSelf> : GeneralStateTestBase
{
    [TestCaseSource(nameof(LoadTests))]
    public void Test(GeneralStateTest test) => RunTest(test).Pass.Should().BeTrue();

    public static IEnumerable<GeneralStateTest> LoadTests() =>
        new TestsSourceLoader(new LoadPyspecTestsStrategy
        {
            ArchiveVersion = Constants.BalArchiveVersion,
            ArchiveName = Constants.BalArchiveName
        }, "fixtures/state_tests", typeof(TSelf).GetCustomAttribute<EipWildcardAttribute>()!.Wildcard).LoadTests<GeneralStateTest>();
}
