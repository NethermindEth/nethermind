// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Ethereum.Test.Base;
using Nethermind.Core.Extensions;
using Nethermind.Stateless.Execution;
using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Test.ZkEvmPreview;

public abstract class ZkEvmBlockchainTestFixture : PyspecLinuxX64BlockchainFixture
{
    protected ZkEvmBlockchainTestFixture() : base(parallel: false, batchRead: false) { }

    [TestCaseSource(nameof(LoadStatelessTests))]
    public void StatelessExecutorOutputMatchesFixture(string inputBytes, string expectedOutputBytes)
    {
        if (!inputBytes.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"StatelessInputBytes must be 0x-prefixed.");

        if (!expectedOutputBytes.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"StatelessOutputBytes must be 0x-prefixed.");

        byte[] actualOutput = StatelessExecutor.Execute(Convert.FromHexString(inputBytes[2..]));
        byte[] expectedOutput = Convert.FromHexString(expectedOutputBytes[2..]);

        Assert.That(actualOutput, Is.EqualTo(expectedOutput),
            $"Expected {expectedOutput.ToHexString(true)}, got {actualOutput.ToHexString(true)}");
    }

    private static IEnumerable<BlockchainTest> LoadTests() => new TestsSourceLoader(
            new LoadPyspecTestsStrategy
            {
                ArchiveName = "fixtures_zkevm.tar.gz",
                ArchiveVersion = "tests-zkevm@v0.5.0"
            },
            "fixtures/blockchain_tests"
        )
        .LoadTests<BlockchainTest>();

    private static IEnumerable<TestCaseData> LoadStatelessTests() => CreateStatelessTestCases(LoadTests());

    private static IEnumerable<TestCaseData> CreateStatelessTestCases(IEnumerable<BlockchainTest> tests)
    {
        foreach (BlockchainTest test in tests)
        {
            if (test.Blocks is not { Length: > 0 } blocks)
                continue;

            for (int i = 0; i < blocks.Length; i++)
            {
                TestBlockJson block = blocks[i];

                if (block.StatelessInputBytes is null && block.StatelessOutputBytes is null)
                    continue;

                if (block.StatelessInputBytes is null || block.StatelessOutputBytes is null)
                    throw new InvalidDataException($"Incomplete stateless fixture data in {test.Name}, block {i}.");

                yield return new TestCaseData(block.StatelessInputBytes, block.StatelessOutputBytes)
                    .SetName($"{test.Name}_stateless_block_{i}");
            }
        }
    }
}
