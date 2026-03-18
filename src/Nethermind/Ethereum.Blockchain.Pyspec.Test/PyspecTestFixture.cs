// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using FluentAssertions;
using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Test;

/// <summary>
/// Generic base for pyspec blockchain tests using <see cref="LoadPyspecTestsStrategy"/>.
/// Directory is derived by convention: strip "BlockchainTests" suffix, lowercase.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class PyspecBlockchainTestFixture<TSelf> : BlockchainTestBase
{
    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => (await RunTest(test)).Pass.Should().BeTrue();

    public static IEnumerable<BlockchainTest> LoadTests() =>
        new TestsSourceLoader(new LoadPyspecTestsStrategy(),
            $"fixtures/blockchain_tests/{TestDirectoryHelper.GetDirectoryByConvention<TSelf>("BlockchainTests")}").LoadTests<BlockchainTest>();
}

/// <summary>
/// Generic base for pyspec state tests using <see cref="LoadPyspecTestsStrategy"/>.
/// Directory is derived by convention: strip "StateTests" suffix, lowercase.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class PyspecStateTestFixture<TSelf> : GeneralStateTestBase
{
    [TestCaseSource(nameof(LoadTests))]
    public void Test(GeneralStateTest test) => RunTest(test).Pass.Should().BeTrue();

    public static IEnumerable<GeneralStateTest> LoadTests() =>
        new TestsSourceLoader(new LoadPyspecTestsStrategy(),
            $"fixtures/state_tests/{TestDirectoryHelper.GetDirectoryByConvention<TSelf>("StateTests")}").LoadTests<GeneralStateTest>();
}
