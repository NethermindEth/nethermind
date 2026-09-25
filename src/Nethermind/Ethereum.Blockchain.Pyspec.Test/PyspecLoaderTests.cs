// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using Nethermind.Core.Test;
using Nethermind.Core.Test.IO;
using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Test;

[NonParallelizable]
public class PyspecLoaderTests
{
    private TempPath _directory;
    private LoadPyspecTestsStrategy _strategy;
    private string _file;
    private string _testChunk;

    [SetUp]
    public void SetUp()
    {
        _testChunk = Environment.GetEnvironmentVariable("TEST_CHUNK");
        Environment.SetEnvironmentVariable("TEST_CHUNK", null);
        _directory = TempPath.GetTempDirectory(Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid().ToString()));
        string archiveRoot = Path.Combine(_directory.Path, "local");
        string fixtures = Path.Combine(archiveRoot, "blockchain_tests", "cases");
        Directory.CreateDirectory(fixtures);
        File.WriteAllText(Path.Combine(archiveRoot, ".completed"), string.Empty);
        // An absolute ArchiveVersion makes Path.Combine discard the cache root, pointing the download at the pre-completed archive above.
        _strategy = new LoadPyspecTestsStrategy { ArchiveVersion = _directory.Path, ArchiveName = "local.tar.gz" };
        _file = Path.Combine(fixtures, "fixture.json");
        File.WriteAllText(_file, """
            {
              "sample": {
                "network": "Cancun",
                "lastblockhash": "0x0000000000000000000000000000000000000000000000000000000000000000",
                "pre": {},
                "blocks": [{"statelessInputBytes": "0x01", "statelessOutputBytes": "0x02"}]
              }
            }
            """);
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable("TEST_CHUNK", _testChunk);
        _directory?.Dispose();
    }

    [TestCase(true, "FileNotFoundException")]
    [TestCase(false, "JsonException")]
    public void Execution_reports_original_load_failure(bool delete, string error)
    {
        PyspecTestRef testRef = (PyspecTestRef)PyspecLoader.LoadCases<BlockchainTest>(_strategy, "blockchain_tests").Single().Arguments[0];
        if (delete)
            File.Delete(_file);
        else
            File.WriteAllText(_file, "{");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => PyspecLoader.LoadTest<BlockchainTest>(testRef));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.Contain(_file));
            Assert.That(exception.Message, Does.Contain(error));
        }
    }

    [Test]
    public void Stateless_cases_load_without_engine_fixtures()
    {
        // The index is process-wide, so a zkEVM witness or engine test earlier in the run leaves nothing to check.
        Assume.That(ZkEvmFixtures.ZkEvmMutatedWitnessIndex.MutatedWitnessesByTest.IsValueCreated, Is.False, "engine-fixture index already built");

        TestCaseData testCase = PyspecLoader.LoadZkEvmStatelessCases(_strategy, "blockchain_tests").Single();
        (string input, string output) = PyspecLoader.LoadZkEvmStatelessBytes((PyspecStatelessRef)testCase.Arguments[0]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(testCase.TestName, Is.EqualTo("sample_stateless_block_0"));
            Assert.That(input, Is.EqualTo("0x01"));
            Assert.That(output, Is.EqualTo("0x02"));
            Assert.That(ZkEvmFixtures.ZkEvmMutatedWitnessIndex.MutatedWitnessesByTest.IsValueCreated, Is.False, "engine-fixture index built");
        }
    }

    [TestCase(null)]
    [TestCase("2of3")]
    public void Discovery_matches_eager_case_identity(string chunk)
    {
        WriteFixtureTree();
        Environment.SetEnvironmentVariable("TEST_CHUNK", chunk);
        string root = _strategy.ResolveTestsRoot("blockchain_tests");
        List<string> directories = [.. Directory.EnumerateDirectories(root, "*", new EnumerationOptions { RecurseSubdirectories = true })];
        BlockchainTest[] eager = [.. TestChunkFilter.FilterByChunk(TestLoadStrategy.LoadTestsFromDirectories(directories, null, TestType.Blockchain).OfType<BlockchainTest>())];
        TestCaseData[] cases = [.. PyspecLoader.LoadCases<BlockchainTest>(_strategy, "blockchain_tests")];

        Assert.That(cases.Select(static c => c.TestName), Is.EqualTo(eager.Select(static (test, index) =>
            string.IsNullOrEmpty(test.Category) ? $"{test.Name}#{index}" : $"{test.Category}/{test.Name}#{index}")));
        for (int i = 0; i < cases.Length; i++)
        {
            BlockchainTest loaded = PyspecLoader.LoadTest<BlockchainTest>((PyspecTestRef)cases[i].Arguments[0]);
            Assert.That((loaded.Category, loaded.Name), Is.EqualTo((eager[i].Category, eager[i].Name)), cases[i].TestName);
        }
    }

    [Test]
    public void Unloadable_file_keeps_failing_case([Values] bool stateless)
    {
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_file)!, "broken.json"), "{");
        TestCaseData[] cases = stateless
            ? [.. PyspecLoader.LoadZkEvmStatelessCases(_strategy, "blockchain_tests")]
            : [.. PyspecLoader.LoadCases<BlockchainTest>(_strategy, "blockchain_tests")];
        TestCaseData broken = cases.Single(static test => test.TestName.Contains("broken.json"));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
        {
            if (stateless)
                PyspecLoader.LoadZkEvmStatelessBytes((PyspecStatelessRef)broken.Arguments[0]);
            else
                PyspecLoader.LoadTest<BlockchainTest>((PyspecTestRef)broken.Arguments[0]);
        });
        Assert.That(exception.Message, Does.Contain("JsonException"));
    }

    [Test]
    public void Stateless_discovery_resolves_each_block()
    {
        File.WriteAllText(_file, $$"""
            {
              "first": {"network":"Cancun","lastblockhash":"0x{{new string('0', 64)}}","pre":{},"blocks":[{"statelessInputBytes":"0x01","statelessOutputBytes":"0x02"},{"statelessInputBytes":"0x03","statelessOutputBytes":"0x04"}]},
              "second": {"network":"Cancun","lastblockhash":"0x{{new string('0', 64)}}","pre":{},"blocks":[{"statelessInputBytes":"0x05","statelessOutputBytes":"0x06"}]}
            }
            """);
        TestCaseData[] cases = [.. PyspecLoader.LoadZkEvmStatelessCases(_strategy, "blockchain_tests")];
        Assert.That(cases.Select(static test => test.TestName), Is.EqualTo(new[] { "first_stateless_block_0", "first_stateless_block_1", "second_stateless_block_0" }));
        Assert.That(cases.Select(static test => PyspecLoader.LoadZkEvmStatelessBytes((PyspecStatelessRef)test.Arguments[0])),
            Is.EqualTo(new[] { ("0x01", "0x02"), ("0x03", "0x04"), ("0x05", "0x06") }));
    }

    [Test]
    public void Execution_reads_only_selected_property()
    {
        string body = $$"""{"network":"Cancun","lastblockhash":"0x{{new string('0', 64)}}","pre":{},"blocks":[]}""";
        File.WriteAllText(_file, $"{{\"first\":{body},\"second\":{body}}}");
        TestCaseData[] cases = [.. PyspecLoader.LoadCases<BlockchainTest>(_strategy, "blockchain_tests")];
        Assert.That(cases, Has.Length.EqualTo(2));

        string json = File.ReadAllText(_file);
        int second = json.IndexOf("\"second\"", StringComparison.Ordinal);
        File.WriteAllText(_file, json[..second] + "!" + json[(second + 1)..]);

        BlockchainTest loaded = PyspecLoader.LoadTest<BlockchainTest>((PyspecTestRef)cases[0].Arguments[0]);
        Assert.That(loaded.Name, Is.EqualTo("first"));
    }

    [Test]
    public void Expanded_transaction_cases_resolve_their_fork()
    {
        string directory = Path.Combine(_directory.Path, "local", "transaction_tests", "cases");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "transactions.json"), """
            {
              "first": {"txbytes":"0x01","result":{"Cancun":{"exception":"first_error"},"Prague":{"exception":"second_error"}}},
              "second": {"txbytes":"0x02","result":{"Cancun":{"exception":"third_error"}}}
            }
            """);

        TestCaseData[] cases = [.. PyspecLoader.LoadCases<TransactionTest>(_strategy, "transaction_tests")];
        Assert.That(cases.Select(static test => test.TestName), Is.EqualTo(new[] { "first::Cancun#0", "first::Prague#1", "second::Cancun#2" }));
        Assert.That(cases.Select(static test => PyspecLoader.LoadTest<TransactionTest>((PyspecTestRef)test.Arguments[0]).ExpectedException),
            Is.EqualTo(new[] { "first_error", "second_error", "third_error" }));
    }

    private void WriteFixtureTree()
    {
        string root = _strategy.ResolveTestsRoot("blockchain_tests");
        string[] directories = [Path.GetDirectoryName(_file)!, Path.Combine(root, "nested", "cases"), Path.Combine(root, "other")];
        foreach (string directory in directories)
        {
            Directory.CreateDirectory(directory);
            for (int file = 0; file < 2; file++)
            {
                string name = $"fixture_{file}.json";
                string cases = string.Join(",", Enumerable.Range(0, 3).Select(index =>
                    $"\"tests/fork/eip_{file}/test.py::case_{file}_{index}\": {{\"network\":\"Cancun\",\"lastblockhash\":\"0x{new string('0', 64)}\",\"pre\":{{}},\"blocks\":[{{\"statelessInputBytes\":\"0x01\",\"statelessOutputBytes\":\"0x02\"}}]}}"));
                File.WriteAllText(Path.Combine(directory, name), $"{{{cases}}}");
            }
        }
        File.WriteAllText(Path.Combine(root, "ignored.json"), "{");
    }

    [Test]
    public async Task Concurrent_discovery_shares_initialization_failure([Values] bool stateless)
    {
        LoadPyspecTestsStrategy strategy = new() { ArchiveVersion = null, ArchiveName = Guid.NewGuid().ToString() };
        using Barrier barrier = new(8);
        Task<Exception>[] tasks = Enumerable.Range(0, 8).Select(worker => Task.Factory.StartNew(() =>
        {
            if (!barrier.SignalAndWait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("Concurrent discovery did not start in time.");
            try
            {
                // An invalid archive version fails inside the cached factory for either source.
                IEnumerable<TestCaseData> cases = stateless
                    ? PyspecLoader.LoadZkEvmStatelessCases(strategy, "blockchain_tests")
                    : PyspecLoader.LoadCases<BlockchainTest>(strategy, "blockchain_tests");
                _ = cases.ToArray();
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        Exception[] failures = await Task.WhenAll(tasks);
        Assert.That(failures[0], Is.TypeOf<ArgumentNullException>());
        Assert.That(failures, Is.All.SameAs(failures[0]));
    }
}
