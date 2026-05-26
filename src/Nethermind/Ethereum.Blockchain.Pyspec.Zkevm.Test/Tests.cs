// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Zkevm.Test;

file static class SkippedTests
{
    public static readonly HashSet<string> Names =
    [
        "test_bal_7002_partial_sweep[fork_Amsterdam-blockchain_test_engine]",
        "test_bal_7702_delegated_storage_access[fork_Amsterdam-blockchain_test_engine]",
        "test_bal_7702_delegation_clear[fork_Amsterdam-blockchain_test_engine-self_funded]",
        "test_bal_7702_delegation_update[fork_Amsterdam-blockchain_test_engine-self_funded]",
        "test_bal_create2_collision[fork_Amsterdam-blockchain_test_engine]",
    ];
}

[TestFixture(false)]
[TestFixture(true)]
public class Eip7928BlockChainTests(bool parallel) : ZkEvmBlockChainTestFixture
{
    protected override bool? ParallelExecutionOverride => parallel;

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test)
    {
        if (SkippedTests.Names.Contains(test.Name))
            Assert.Ignore($"Test '{test.Name}' is temporarily skipped pending investigation.");
        Assert.That((await RunTest(test)).Pass, Is.True);
    }

    public static IEnumerable<BlockchainTest> LoadTests() =>
        LoadBlockChainTests("eip7928_block_level_access_lists");
}

[TestFixture(false)]
[TestFixture(true)]
public class Eip7928EngineBlockChainTests(bool parallel) : ZkEvmBlockChainTestFixture
{
    protected override bool? ParallelExecutionOverride => parallel;

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test)
    {
        if (SkippedTests.Names.Contains(test.Name))
            Assert.Ignore($"Test '{test.Name}' is temporarily skipped pending investigation.");
        Assert.That((await RunTest(test)).Pass, Is.True);
    }

    public static IEnumerable<BlockchainTest> LoadTests() =>
        LoadEngineBlockChainTests("eip7928_block_level_access_lists");
}

[TestFixture(false)]
[TestFixture(true)]
public class Eip7928WitnessEngineBlockChainTests(bool parallel) : ZkEvmWitnessEngineBlockChainTestFixture
{
    protected override bool? ParallelExecutionOverride => parallel;

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test)
    {
        if (SkippedTests.Names.Contains(test.Name))
            Assert.Ignore($"Test '{test.Name}' is temporarily skipped pending investigation.");
        Assert.That((await RunTest(test)).Pass, Is.True);
    }

    public static IEnumerable<BlockchainTest> LoadTests() =>
        LoadWitnessEngineBlockChainTests("eip7928_block_level_access_lists");
}
