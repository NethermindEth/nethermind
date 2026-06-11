// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Ethereum.Test.Base;
using Nethermind.Core.Extensions;
using Nethermind.Stateless.Execution;

namespace Nethermind.Test.Runner;

/// <summary>
/// Runs the zkEVM stateless-execution preview fixtures (the <c>tests-zkevm</c> releases):
/// blockchain-test-shaped fixtures whose blocks carry stateless witness input/output bytes.
/// </summary>
/// <remarks>
/// Each block with stateless data becomes one test case: the input bytes are fed through
/// <see cref="StatelessExecutor"/> and the produced output is byte-compared with the
/// fixture's expected output, mirroring the ZkEvmPreview NUnit fixture.
/// </remarks>
public static class ZkEvmTestsRunner
{
    public static List<EthereumTestResult> RunTest(BlockchainTest test)
    {
        List<EthereumTestResult> results = [];
        if (test.Blocks is not { Length: > 0 } blocks)
            return results;

        for (int i = 0; i < blocks.Length; i++)
        {
            TestBlockJson block = blocks[i];
            if (block.StatelessInputBytes is null && block.StatelessOutputBytes is null)
                continue;

            string? error = ExecuteCase(block);
            results.Add(new EthereumTestResult($"{test.Name}_stateless_block_{i}", test.ForkName, error is null) { Error = error });
        }

        return results;
    }

    private static string? ExecuteCase(TestBlockJson block)
    {
        string? inputBytes = block.StatelessInputBytes;
        string? expectedOutputBytes = block.StatelessOutputBytes;

        if (inputBytes is null || expectedOutputBytes is null)
            return "Incomplete stateless fixture data: both StatelessInputBytes and StatelessOutputBytes are required.";

        if (!inputBytes.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return "StatelessInputBytes must be 0x-prefixed.";

        if (!expectedOutputBytes.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return "StatelessOutputBytes must be 0x-prefixed.";

        try
        {
            byte[] actualOutput = StatelessExecutor.Execute(Convert.FromHexString(inputBytes.AsSpan(2)));
            byte[] expectedOutput = Convert.FromHexString(expectedOutputBytes.AsSpan(2));

            return Bytes.AreEqual(actualOutput, expectedOutput)
                ? null
                : $"Expected {expectedOutput.ToHexString(true)}, got {actualOutput.ToHexString(true)}";
        }
        catch (Exception ex)
        {
            return $"Stateless execution threw: {ex.Message}";
        }
    }
}
