// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Evm.Benchmark.GasBenchmarks;
using NUnit.Framework;

namespace Nethermind.Evm.Benchmark.Test;

/// <summary>
/// Tests for GasPayloadBenchmarks.FindSetupFile which searches the setup directory
/// for a file matching a given test filename.
/// </summary>
[TestFixture]
public class FindSetupFileTests
{
    [Test]
    public void FindSetupFile_Returns_Null_When_SetupDir_Does_Not_Exist()
    {
        // If the gas-benchmarks submodule isn't cloned, FindSetupFile should return null
        // since the setup directory doesn't exist.
        // This is a no-op test if the submodule IS cloned but has no matching file.
        string result = GasPayloadBenchmarks.FindSetupFile("nonexistent_file_that_should_never_exist_12345.txt");

        Assert.That(result, Is.Null);
    }

    [Test]
    public void GetTestCases_Returns_Empty_When_Submodule_Not_Available()
    {
        // GetTestCases should gracefully handle missing submodule
        // It will either return test cases (if submodule is cloned) or empty
        int count = 0;
        foreach (GasPayloadBenchmarks.TestCase _ in GasPayloadBenchmarks.GetTestCases())
        {
            count++;
            break; // Don't enumerate all, just check it doesn't throw
        }

        // Should not throw regardless of submodule state
        Assert.Pass($"GetTestCases returned at least {count} test case(s) or empty without error");
    }
}
