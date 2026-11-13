// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Stateless.Execution;
using Nethermind.Stateless.Execution.IO;

namespace Nethermind.Test.Runner;

/// <summary>
/// Runs the zkEVM stateless-execution preview fixtures (the <c>tests-zkevm</c> releases):
/// blockchain-test-shaped fixtures whose blocks carry stateless witness input/output bytes.
/// </summary>
/// <remarks>
/// Each block with stateless data becomes two test cases, mirroring the ZkEvmPreview NUnit fixture: the input bytes
/// are fed through <see cref="StatelessExecutor.Execute"/> and the produced output is byte-compared with the fixture's
/// expected output, and the same block goes as a trusted input through <see cref="StatelessExecutor.ExecuteTrusted"/>,
/// whose verdict must match the fixture's.
/// </remarks>
public static class ZkEvmTestsRunner
{
    public static List<EthereumTestResult> RunTest(BlockchainTest test)
    {
        if (test.LoadFailure is not null)
            return [new EthereumTestResult(test.Name, test.ForkName, test.LoadFailure)];

        List<EthereumTestResult> results = [];
        if (test.Blocks is not { Length: > 0 } blocks)
            return results;

        for (int i = 0; i < blocks.Length; i++)
        {
            TestBlockJson block = blocks[i];
            if (block.StatelessInputBytes is null && block.StatelessOutputBytes is null)
                continue;

            (byte[]? input, byte[]? expectedOutput, string? error) = ReadCase(block);
            results.Add(Result($"{test.Name}_stateless_block_{i}", test.ForkName, error ?? ExecuteCase(input!, expectedOutput!)));
            results.Add(Result($"{test.Name}_trusted_block_{i}", test.ForkName, error ?? ExecuteTrustedCase(input!, expectedOutput!)));
        }

        return results;
    }

    public static int CountCases(BlockchainTest test)
    {
        if (test.LoadFailure is not null)
            return 1;

        if (test.Blocks is not { Length: > 0 } blocks)
            return 0;

        int count = 0;
        foreach (TestBlockJson block in blocks)
        {
            if (block.StatelessInputBytes is not null || block.StatelessOutputBytes is not null)
                count += 2;
        }

        return count;
    }

    private static EthereumTestResult Result(string name, string? fork, string? error) => new(name, fork, error is null) { Error = error };

    private static (byte[]? Input, byte[]? ExpectedOutput, string? Error) ReadCase(TestBlockJson block)
    {
        string? inputBytes = block.StatelessInputBytes;
        string? expectedOutputBytes = block.StatelessOutputBytes;

        if (inputBytes is null || expectedOutputBytes is null)
            return (null, null, "Incomplete stateless fixture data: both StatelessInputBytes and StatelessOutputBytes are required.");

        if (!inputBytes.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return (null, null, "StatelessInputBytes must be 0x-prefixed.");

        if (!expectedOutputBytes.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return (null, null, "StatelessOutputBytes must be 0x-prefixed.");

        try
        {
            return (Convert.FromHexString(inputBytes.AsSpan(2)), Convert.FromHexString(expectedOutputBytes.AsSpan(2)), null);
        }
        catch (FormatException ex)
        {
            return (null, null, $"Stateless fixture data is not hex: {ex.Message}");
        }
    }

    private static string? ExecuteCase(byte[] input, byte[] expectedOutput)
    {
        try
        {
            byte[] actualOutput = StatelessExecutor.Execute(input);

            return Bytes.AreEqual(actualOutput, expectedOutput)
                ? null
                : $"Expected {expectedOutput.ToHexString(true)}, got {actualOutput.ToHexString(true)}";
        }
        catch (Exception ex)
        {
            return $"Stateless execution threw: {ex.Message}";
        }
    }

    /// <remarks>
    /// The trusted input carries the stateless input's block and witness but not its engine API envelope, the claimed
    /// block hash and the versioned hashes, so it must reach the same verdict unless the envelope alone decided it.
    /// </remarks>
    private static string? ExecuteTrustedCase(byte[] statelessInput, byte[] expectedOutput)
    {
        try
        {
            StatelessValidationResult.Decode(expectedOutput, out StatelessValidationResult expected);
            StatelessPayload payload;
            Block block;
            byte[] trustedInput;
            try
            {
                payload = InputDecoder.Decode(statelessInput);
                block = payload.GetBlock();
                trustedInput = TrustedInput.FromStatelessInput(statelessInput);
            }
            catch (Exception) when (!expected.IsSuccess)
            {
                // A block that does not decode has no trusted input.
                return null;
            }

            StatelessValidationResult.Decode(StatelessExecutor.ExecuteTrusted(trustedInput), out StatelessValidationResult actual);
            Hash256 headerHash = block.Header.CalculateHash();
            bool decidedByEnvelope = headerHash != block.Hash
                || !StatelessExecutor.BlobVersionedHashesMatch(block.Transactions, payload.VersionedHashes.Span);
            if ((expected.IsSuccess || !decidedByEnvelope) && actual.IsSuccess != expected.IsSuccess)
                return $"Trusted verdict {actual.IsSuccess}, expected {expected.IsSuccess}";

            return actual.NewPayloadRequestRoot == headerHash
                ? null
                : $"Trusted result root {actual.NewPayloadRequestRoot}, expected the header hash {headerHash}";
        }
        catch (Exception ex)
        {
            return $"Trusted execution threw: {ex.Message}";
        }
    }
}
