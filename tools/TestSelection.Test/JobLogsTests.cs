// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.TestSelection.Collection;
using Nethermind.TestSelection.Journal;
using NUnit.Framework;

namespace Nethermind.TestSelection.Test;

[TestFixture]
public class JobLogsTests
{
    [TestCase("Run Nethermind.Evm.Test (ubuntu-latest)", "Nethermind.Evm.Test", TestName = "Project")]
    [TestCase("Run Nethermind.Synchronization.Test (1of4) (ubuntu-latest)", "Nethermind.Synchronization.Test", TestName = "Chunk")]
    [TestCase("Run Nethermind.Core.Test [avx2]", "Nethermind.Core.Test", TestName = "IsaLane")]
    [TestCase("Run Nethermind tests 3 (ubuntu-latest)", "Nethermind tests 3", TestName = "Pack")]
    [TestCase("Run Nethermind tests 1 [checked]", "Nethermind tests 1", TestName = "PackInALane")]
    [TestCase("EEST nethtest / nethtest blockTest sequential 1of4", "EEST", TestName = "Eest")]
    [TestCase("Hive tests / Run Hive (engine-api)", "Hive", TestName = "Hive")]
    [TestCase("build / Build release (linux-x64)", null, TestName = "Build")]
    public void UnitOf_NamesWhatAJobRuns(string job, string? unit) => Assert.That(JobLogs.UnitOf(job), Is.EqualTo(unit), "the unit decides how its log is read");

    [TestCase("EEST nethtest / nethtest blockTest sequential 1of4", "blockTest|sequential", TestName = "Prefixed")]
    [TestCase("nethtest stateTest all 2of2", "stateTest|all", TestName = "Plain")]
    [TestCase("EEST nethtest / Prepare nethtest matrix", null, TestName = "NotALane")]
    public void NethtestLane_ReadsTypeAndSetup(string job, string? lane) => Assert.That(JobLogs.NethtestLane(job), Is.EqualTo(lane), "a lane is type and setup");

    [Test]
    public void TestFailures_ReadEestFailures()
    {
        string log = "2026-10-01T00:00:00.0Z FAIL [3/9] test_add[fork_Prague-blockchain_test]\n2026-10-01T00:00:00.0Z \u001b[31mEXCEPTION test_mul[fork_Cancun]\u001b[0m\n";
        Assert.That(JobLogs.TestFailures(log, "EEST nethtest / nethtest blockTest sequential 1of4"),
            Is.EquivalentTo(new[] { "EEST|blockTest|sequential|test_add|Prague", "EEST|blockTest|sequential|test_mul|Cancun" }), "both failure kinds, colours stripped");
    }

    [Test]
    public void TestFailures_InAPack_BelongToTheProjectOfTheirLogGroup()
    {
        string log = string.Join('\n',
            "2026-10-01T00:00:00.0Z ##[group]Nethermind.Abi.Test",
            "2026-10-01T00:00:01.0Z   failed Nethermind.Abi.Test.AbiTests.Encode",
            "2026-10-01T00:00:02.0Z ##[endgroup]",
            "2026-10-01T00:00:03.0Z ##[group]Nethermind.Api.Test",
            "2026-10-01T00:00:04.0Z   failed Nethermind.Api.Test.ApiTests.Start",
            "2026-10-01T00:00:05.0Z ##[endgroup]");
        Assert.That(JobLogs.TestFailures(log, "Run Nethermind tests 1 (ubuntu-latest)"),
            Is.EquivalentTo(new[] { "Nethermind.Abi.Test|Nethermind.Abi.Test.AbiTests.Encode", "Nethermind.Api.Test|Nethermind.Api.Test.ApiTests.Start" }),
            "a pack's job name names no project");
    }

    [Test]
    public void GroupSeconds_TimeEachProjectOfAPack()
    {
        string log = string.Join('\n',
            "2026-10-01T00:00:00.0000000Z ##[group]Run bash scripts/ci/run-test-projects.sh",
            "2026-10-01T00:00:01.0000000Z ##[endgroup]",
            "2026-10-01T00:00:01.0000000Z ##[group]Nethermind.Abi.Test",
            "2026-10-01T00:01:31.0000000Z ##[endgroup]",
            "2026-10-01T00:01:31.0000000Z ##[group]Nethermind.Api.Test",
            "2026-10-01T00:01:41.0000000Z ##[endgroup]");
        Assert.That(JobLogs.GroupSeconds(log), Is.EquivalentTo(new Dictionary<string, double> { ["Nethermind.Abi.Test"] = 90, ["Nethermind.Api.Test"] = 10 }),
            "the step's own group is no project");
    }

    [Test]
    public async Task Classify_JudgesEestApart_AndCallsJobsWithoutTestFailuresInfra()
    {
        FailedJob eest = new(1, "EEST nethtest / nethtest blockTest sequential 1of4");
        FailedJob unit = new(2, "Run Nethermind.Evm.Test (ubuntu-latest)");
        Dictionary<long, string> logs = new() { [1] = "Z FAIL test_add[fork_Prague]", [2] = "##[error]The process '/usr/bin/git' failed with exit code 128" };

        Failures failures = await FailureClassifier.ClassifyAsync([eest, unit], job => Task.FromResult(logs[job.Id]));

        Assert.That(failures.Tests, Is.EqualTo(new[] { "EEST|blockTest|sequential|test_add|Prague" }), "the EEST failure is a test failure");
        Assert.That(failures.Infra, Is.EqualTo(new[] { "Nethermind.Evm.Test" }), "a checkout failure is infra, not hidden by the EEST failure");
    }

    [Test]
    public async Task Classify_WhenTheSampleOfAManyJobGroupShowsNoTestFailure_ReadsNoMoreLogs()
    {
        FailedJob[] jobs = [.. Enumerable.Range(1, FailureClassifier.InfraSample * 3 + 1).Select(static i => new FailedJob(i, $"Run Nethermind.P{i}.Test (ubuntu-latest)"))];
        int read = 0;

        Failures failures = await FailureClassifier.ClassifyAsync(jobs, _ => { read++; return Task.FromResult("runner lost"); });

        Assert.That(read, Is.EqualTo(FailureClassifier.InfraSample), "the sample decides it was a runner problem");
        Assert.That(failures.Infra.Count, Is.EqualTo(jobs.Length), "every job of the group counts as infra");
    }
}
