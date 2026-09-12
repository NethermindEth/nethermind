// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Test.Runner;
using NUnit.Framework;

namespace Nethermind.State.Test.Runner.Test;

/// <summary>
/// A state-test fixture that cannot be parsed must be reported as a failure rather than vanish
/// from the run, which previously left <c>nethtest --stateTest</c> printing an empty result array.
/// </summary>
public class StateTestLoadFailureTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    [TestCase("{\"broken\": ", TestName = "Malformed JSON")]
    [TestCase("{\"t\":{\"transaction\":{\"value\":[\"0x\"]}}}", TestName = "Deserializer rejects a quantity")]
    [TestCase("{\"t\":{\"env\":{},\"pre\":{},\"transaction\":{}}}", TestName = "Conversion to a test case throws")]
    public void Fixture_that_fails_to_parse_survives_the_state_test_type_filter(string content)
    {
        string file = Path.Combine(_directory, "fixture.json");
        File.WriteAllText(file, content);

        List<GeneralStateTest> tests = [.. new TestsSourceLoader(new LoadGeneralStateTestFileStrategy(), file).LoadTests<GeneralStateTest>()];

        Assert.That(tests, Has.Count.EqualTo(1));
        Assert.That(tests[0].LoadFailure, Is.Not.Null);
    }

    [Test]
    public void Runner_reports_a_load_failure_instead_of_running_the_test()
    {
        StateTestsRunner runner = new(WhenTrace.Never, traceMemory: false, traceStack: false, chainId: BlockchainIds.Mainnet, suppressOutput: true);
        GeneralStateTest test = new() { Name = "fixture.json", LoadFailure = "Failed to load: boom" };

        EthereumTestResult result = runner.RunSingleTest(test);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Pass, Is.False);
            Assert.That(result.Error, Is.EqualTo("Failed to load: boom"));
        }
    }
}
