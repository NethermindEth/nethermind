// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Zkevm.Test;

file static class KnownFailingTests
{
    public static readonly HashSet<string> Names = Load();

    private static HashSet<string> Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "known-failing-zkevm-tests.txt");
        if (!File.Exists(path))
            return [];

        return File.ReadLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToHashSet();
    }
}

[TestFixture(false)]
[TestFixture(true)]
public class Eip7928BlockChainTests(bool parallel) : ZkEvmBlockChainTestFixture
{
    protected override bool? ParallelExecutionOverride => parallel;

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test)
    {
        if (KnownFailingTests.Names.Contains(test.Name))
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
        if (KnownFailingTests.Names.Contains(test.Name))
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
        if (KnownFailingTests.Names.Contains(test.Name))
            Assert.Ignore($"Test '{test.Name}' is temporarily skipped pending investigation.");
        Assert.That((await RunTest(test)).Pass, Is.True);
    }

    public static IEnumerable<BlockchainTest> LoadTests() =>
        LoadWitnessEngineBlockChainTests("eip7928_block_level_access_lists");
}
