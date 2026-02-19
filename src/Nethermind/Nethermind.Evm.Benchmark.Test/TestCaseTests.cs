// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Evm.Benchmark.GasBenchmarks;
using NUnit.Framework;

namespace Nethermind.Evm.Benchmark.Test;

[TestFixture]
public class TestCaseTests
{
    [TestCase(
        "tests_benchmark_compute_instruction_test_foo.py__test_bar[fork_Prague-benchmark-blockchain_test_engine_x-param1-param2]-gas-value_100M.txt",
        "bar[param1-param2]")]
    [TestCase(
        "tests_benchmark_compute_instruction_test_foo.py__test_simple_op-gas-value_100M.txt",
        "simple_op")]
    [TestCase(
        "tests_benchmark_compute_instruction_test_foo.py__test_single[fork_Prague-benchmark-blockchain_test_engine_x-]-gas-value_100M.txt",
        "single")]
    [TestCase(
        "no_test_marker_in_this_file.txt",
        "no_test_marker_in_this_file.txt")]
    [TestCase(
        "tests_benchmark_compute_instruction_test_opcodes.py__test_MULMOD[fork_Prague-benchmark-blockchain_test_engine_x-mod_bits_63]-gas-value_100M.txt",
        "MULMOD[mod_bits_63]")]
    [TestCase(
        "tests_benchmark_compute_instruction_test_x.py__test_a_to_a-gas-value_100M.txt",
        "a_to_a")]
    public void ExtractShortName_Produces_Expected_DisplayName(string fileName, string expectedDisplayName)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "benchmark_test_" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            string filePath = Path.Combine(tempDir, fileName);
            File.WriteAllText(filePath, "dummy");

            GasPayloadBenchmarks.TestCase testCase = new(filePath);

            Assert.That(testCase.DisplayName, Is.EqualTo(expectedDisplayName));
            Assert.That(testCase.FileName, Is.EqualTo(fileName));
            Assert.That(testCase.FilePath, Is.EqualTo(filePath));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public void TestCase_ToString_Returns_DisplayName()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "benchmark_test_" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            string filePath = Path.Combine(tempDir, "tests_x.py__test_foo-gas-value_100M.txt");
            File.WriteAllText(filePath, "dummy");

            GasPayloadBenchmarks.TestCase testCase = new(filePath);

            Assert.That(testCase.ToString(), Is.EqualTo(testCase.DisplayName));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public void TestCase_Preserves_Full_FilePath()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "benchmark_test_" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            string filePath = Path.Combine(tempDir, "deep", "nested", "test.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, "dummy");

            GasPayloadBenchmarks.TestCase testCase = new(filePath);

            Assert.That(testCase.FilePath, Is.EqualTo(filePath));
            Assert.That(testCase.FileName, Is.EqualTo("test.txt"));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [TestCase(
        "tests_benchmark_compute_instruction_test_foo.py__test_method[fork_Prague-benchmark-blockchain_test_engine_x-alpha-beta]-gas-value_100M.txt",
        "method[alpha-beta]",
        Description = "Standard two-param scenario")]
    [TestCase(
        "tests_benchmark_compute_instruction_test_foo.py__test_no_gas_suffix.txt",
        "no_gas_suffix.txt",
        Description = "No -gas-value_ suffix means no stripping")]
    public void ExtractShortName_Edge_Cases(string fileName, string expectedDisplayName)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "benchmark_test_" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            string filePath = Path.Combine(tempDir, fileName);
            File.WriteAllText(filePath, "dummy");

            GasPayloadBenchmarks.TestCase testCase = new(filePath);

            Assert.That(testCase.DisplayName, Is.EqualTo(expectedDisplayName));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
