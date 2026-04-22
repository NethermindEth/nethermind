// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using FluentAssertions;
using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Test.Amsterdam;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class AmsterdamBalBlockChainValidationFixture : BlockchainTestBase
{
    [SetUp]
    public void SkipInCiOnSlowRunners() => CiRunnerGuard.SkipIfNotLinuxX64();

    protected async Task Run(BlockchainTest test) => (await RunTest(test)).Pass.Should().BeTrue();

    protected static IEnumerable<BlockchainTest> LoadBlockChainTests(string path, string wildcard) =>
        new TestsSourceLoader(new LoadPyspecTestsStrategy
        {
            ArchiveVersion = Constants.BalArchiveVersion,
            ArchiveName = Constants.BalArchiveName
        }, path, wildcard).LoadTests<BlockchainTest>();
}

[TestFixture]
public sealed class Push0ValidationBlockChainTests : AmsterdamBalBlockChainValidationFixture
{
    [TestCaseSource(nameof(LoadTests))]
    public Task Test(BlockchainTest test) => Run(test);

    public static IEnumerable<BlockchainTest> LoadTests() =>
        LoadBlockChainTests("fixtures/blockchain_tests/for_amsterdam/shanghai/eip3855_push0", "push0_contracts");
}

[TestFixture]
public sealed class ReturnDataValidationBlockChainTests : AmsterdamBalBlockChainValidationFixture
{
    [TestCaseSource(nameof(LoadTests))]
    public Task Test(BlockchainTest test) => Run(test);

    public static IEnumerable<BlockchainTest> LoadTests() =>
        LoadBlockChainTests("fixtures/blockchain_tests/for_amsterdam/ported_static/stReturnDataTest", "too_long_return_data_copy");
}

[TestFixture]
public sealed class WalletValidationBlockChainTests : AmsterdamBalBlockChainValidationFixture
{
    [TestCaseSource(nameof(LoadTests))]
    public Task Test(BlockchainTest test) => Run(test);

    public static IEnumerable<BlockchainTest> LoadTests() =>
        LoadBlockChainTests("fixtures/blockchain_tests/for_amsterdam/ported_static/stWalletTest", "wallet_construction_oog");
}

[TestFixture]
public sealed class MultiOwnedWalletValidationBlockChainTests : AmsterdamBalBlockChainValidationFixture
{
    [TestCaseSource(nameof(LoadTests))]
    public Task Test(BlockchainTest test) => Run(test);

    public static IEnumerable<BlockchainTest> LoadTests() =>
        LoadBlockChainTests("fixtures/blockchain_tests/for_amsterdam/ported_static/stWalletTest", "multi_owned_construction_not_enough_gas_partial");
}
