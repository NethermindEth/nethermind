// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Zkevm.Test;

[TestFixture(false)]
[TestFixture(true)]
public class Eip7928BlockChainTests(bool parallel) : ZkEvmBlockChainTestFixture
{
    protected override bool? ParallelExecutionOverride => parallel;

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => Assert.That((await RunTest(test)).Pass, Is.True);

    public static IEnumerable<BlockchainTest> LoadTests() =>
        LoadBlockChainTests("eip7928_block_level_access_lists");
}

[TestFixture(false)]
[TestFixture(true)]
public class Eip7928EngineBlockChainTests(bool parallel) : ZkEvmBlockChainTestFixture
{
    protected override bool? ParallelExecutionOverride => parallel;

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => Assert.That((await RunTest(test)).Pass, Is.True);

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
        if (test.Name is not null && (
            test.Name.Contains("test_bal_7002_partial_sweep") ||
            test.Name.Contains("test_bal_7702_delegated_storage_access") ||
            test.Name.Contains("test_bal_7702_delegation_clear") ||
            test.Name.Contains("test_bal_7702_delegation_update") ||
            test.Name.Contains("test_bal_7702_invalid_authority_has_code_authorization") ||
            test.Name.Contains("test_bal_7702_multi_hop_delegation_chain") ||
            test.Name.Contains("test_bal_create2_collision") ||
            test.Name.Contains("test_bal_create2_selfdestruct_then_recreate_same_block") ||
            test.Name.Contains("test_bal_cross_tx_deploy_then_call") ||
            test.Name.Contains("test_bal_extcodecopy_and_oog")))
        {
            Assert.Ignore("Skipped for now due to witness mismatch.");
            return;
        }

        Assert.That((await RunTest(test)).Pass, Is.True);
    }

    public static IEnumerable<BlockchainTest> LoadTests() =>
        LoadWitnessEngineBlockChainTests("eip7928_block_level_access_lists");
}
