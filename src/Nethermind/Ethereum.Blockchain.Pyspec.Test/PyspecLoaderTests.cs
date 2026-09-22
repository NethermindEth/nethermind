// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Ethereum.Test.Base;
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
        object witnessIndex = typeof(ZkEvmFixtures.ZkEvmMutatedWitnessIndex)
            .GetField("MutatedWitnessesByTest", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        PropertyInfo isValueCreated = witnessIndex.GetType().GetProperty(nameof(Lazy<object>.IsValueCreated));
        object wasCreated = isValueCreated.GetValue(witnessIndex);

        TestCaseData testCase = PyspecLoader.LoadZkEvmStatelessCases(_strategy, "blockchain_tests").Single();
        (string input, string output) = PyspecLoader.LoadZkEvmStatelessBytes((PyspecStatelessRef)testCase.Arguments[0]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(testCase.TestName, Is.EqualTo("sample_stateless_block_0"));
            Assert.That(input, Is.EqualTo("0x01"));
            Assert.That(output, Is.EqualTo("0x02"));
            Assert.That(isValueCreated.GetValue(witnessIndex), Is.EqualTo(wasCreated));
        }
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

    [Test]
    public void Execution_parallelism_is_bounded() =>
        Assert.That(typeof(PyspecLoader).Assembly.GetCustomAttribute<LevelOfParallelismAttribute>()?.Properties.Get("LevelOfParallelism"), Is.EqualTo(4));
}
