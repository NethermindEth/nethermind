// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using FluentAssertions;
using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Test.Amsterdam;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class AmsterdamTransitionBlockChainTestFixture<TSelf> : BlockchainTestBase
{
    [SetUp]
    public void SkipInCiOnSlowRunners() => CiRunnerGuard.SkipIfNotLinuxX64();

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => (await RunTest(test)).Pass.Should().BeTrue();

    public static IEnumerable<BlockchainTest> LoadTests() =>
        new TestsSourceLoader(new LoadPyspecTestsStrategy
        {
            ArchiveVersion = Constants.BalArchiveVersion,
            ArchiveName = Constants.BalArchiveName
        }, "fixtures/blockchain_tests/for_bpo2toamsterdamattime15k", typeof(TSelf).GetCustomAttribute<EipWildcardAttribute>()!.Wildcard)
        .LoadTests<BlockchainTest>();
}

[EipWildcard("eip7954_increase_max_contract_size")]
public class Eip7954TransitionBlockChainTests : AmsterdamTransitionBlockChainTestFixture<Eip7954TransitionBlockChainTests>;

[EipWildcard("eip8037_state_creation_gas_cost_increase")]
public class Eip8037TransitionBlockChainTests : AmsterdamTransitionBlockChainTestFixture<Eip8037TransitionBlockChainTests>;
