// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Ethereum.Test.Base;
using Nethermind.Test.Runner;
using NUnit.Framework;

namespace Nethermind.State.Test.Runner.Test;

/// <summary>nethtest's transaction and zkEVM modes report a fixture file that cannot be parsed as one failed case.</summary>
/// <remarks><c>TEST_CHUNK</c> is unset for each test, since the loader would otherwise split the single case away.</remarks>
[NonParallelizable]
public class NethtestLoadFailureTests
{
    private const string ChunkVariable = "TEST_CHUNK";

    private string _file = null!;
    private string _testChunk = null!;

    [SetUp]
    public void SetUp()
    {
        _testChunk = Environment.GetEnvironmentVariable(ChunkVariable);
        Environment.SetEnvironmentVariable(ChunkVariable, null);
        _file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(_file, "{\"broken\": ");
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(ChunkVariable, _testChunk);
        File.Delete(_file);
    }

    [Test]
    public void Transaction_mode_reports_an_unreadable_file()
    {
        List<TransactionTest> tests = [.. new TestsSourceLoader(new LoadTransactionTestFileStrategy(), _file).LoadTests<TransactionTest>()];

        Assert.That(tests, Has.Count.EqualTo(1));
        AssertReportsLoadFailure(new TransactionTestsRunner().RunSingleTest(tests[0]), tests[0]);
    }

    [Test]
    public void ZkEvm_mode_counts_and_reports_an_unreadable_file_as_one_case()
    {
        List<BlockchainTest> tests = [.. new TestsSourceLoader(new LoadBlockchainTestFileStrategy(), _file).LoadTests<BlockchainTest>()];

        Assert.That(tests, Has.Count.EqualTo(1));
        List<EthereumTestResult> results = ZkEvmTestsRunner.RunTest(tests[0]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ZkEvmTestsRunner.CountCases(tests[0]), Is.EqualTo(1));
            Assert.That(results, Has.Count.EqualTo(1));
        }

        AssertReportsLoadFailure(results[0], tests[0]);
    }

    private void AssertReportsLoadFailure(EthereumTestResult result, EthereumTest test)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(test.LoadFailure, Does.Contain(nameof(JsonException)));
            Assert.That(result.Name, Is.EqualTo(_file));
            Assert.That(result.Pass, Is.False);
            Assert.That(result.Error, Is.EqualTo(test.LoadFailure));
        }
    }
}
