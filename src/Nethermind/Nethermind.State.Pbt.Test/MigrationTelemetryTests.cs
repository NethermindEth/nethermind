// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.State.Pbt.Migration;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class MigrationTelemetryTests
{
    [Test]
    public async Task Progress_and_shadow_roots_follow_the_native_pbt_state_and_finality()
    {
        using MigrationBalFollowerTests.Harness harness = new();
        harness.CommitFlatGenesis();
        harness.SelectHead("a3");
        await using PbtMigrationImport import = new(_ => Task.CompletedTask, Substitute.For<IProcessExitSource>(), LimboLogs.Instance);
        await using MerkleShadowFollower merkle = harness.CreateMerkleShadowFollower();
        MigrationTelemetry telemetry = CreateTelemetry(harness, import, merkle);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(telemetry.GetProgress(), Is.EqualTo(new MigrationProgressForRpc("running", null, null)));
            Assert.That(telemetry.GetShadowRoot(harness.Blocks["anchor"].Hash!), Is.Null, "no state before the anchor import");
        }
        await harness.Publish();
        Assert.That(telemetry.GetShadowRoot(harness.Blocks["anchor"].Hash!), Is.EqualTo(Eip8347FixtureState.PbtRoot("anchor")));

        // Holding the replay at a3's BAL leaves the follower behind the head.
        using ManualResetEventSlim a3Released = new();
        harness.OnRead = hash =>
        {
            if (hash == harness.Blocks["a3"].Hash) a3Released.Wait(10_000);
        };
        harness.Follower.Start();
        merkle.Start();
        MigrationDirectionForRpc following = new("following", 2, harness.Blocks["a2"].Hash!, Eip8347FixtureState.PbtRoot("a2"), "");
        Assert.That(() => telemetry.GetProgress(), Is.EqualTo(new MigrationProgressForRpc("running", following, null)).After(10_000, 50));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(telemetry.GetShadowRoot(harness.Blocks["a2"].Hash!), Is.EqualTo(Eip8347FixtureState.PbtRoot("a2")));
            Assert.That(telemetry.GetShadowRoot(harness.Blocks["a4"].Hash!), Is.Null, "the post-activation shadow comes from the Merkle follower, which has not replayed yet");
            Assert.That(telemetry.GetShadowRoot(harness.Blocks["a5"].Hash!), Is.Null, "known header without retained state");
            Assert.That(telemetry.GetShadowRoot(Hash256.Zero), Is.Null);
        }

        a3Released.Set();
        Assert.That(() => telemetry.GetProgress().Binary?.Phase, Is.EqualTo("synced").After(10_000, 50));
        AssertFixture("prefork", telemetry.GetProgress());

        harness.SelectHead("a4");
        Hash256 merkleRoot = new(harness.Metadata["a4"].GetProperty("mptRoot").GetString()!);
        Assert.That(() => telemetry.GetProgress().Merkle, Is.EqualTo(new MigrationDirectionForRpc("synced", 4, harness.Blocks["a4"].Hash!, merkleRoot, "")).After(10_000, 50));
        MigrationProgressForRpc transition = telemetry.GetProgress();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(transition.Binary, Is.EqualTo(new MigrationDirectionForRpc("parked", 3, harness.Blocks["a3"].Hash!, Eip8347FixtureState.PbtRoot("a3"), "")));
            Assert.That(telemetry.GetShadowRoot(harness.Blocks["a4"].Hash!), Is.EqualTo(merkleRoot));
            Assert.That(telemetry.GetShadowRoot(harness.Blocks["a3"].Hash!), Is.EqualTo(Eip8347FixtureState.PbtRoot("a3")), "pre-activation shadow stays the PBT root");
        }
        AssertFixture("transition", transition);
        harness.Store.Delete(harness.Blocks["a5"].Number, harness.Blocks["a5"].Hash!);
        harness.SelectHead("a5");
        Assert.That(() => telemetry.GetProgress().Merkle, Is.EqualTo(new MigrationDirectionForRpc("stalled", 4, harness.Blocks["a4"].Hash!, merkleRoot, StalledError)).After(10_000, 50));

        harness.BlockTree.ForkChoiceUpdated(harness.Blocks["a4"].Hash!, Hash256.Zero);
        Assert.That(telemetry.GetProgress(), Is.EqualTo(new MigrationProgressForRpc("done", null, null)));
        AssertFixture("done", telemetry.GetProgress());
    }

    [Test]
    public async Task Lagging_progress_reports_stall_without_inventing_cursor_or_root()
    {
        using MigrationBalFollowerTests.Harness harness = new();
        harness.SelectHead("a1");
        await harness.Publish();
        harness.Store.Delete(harness.Blocks["a1"].Number, harness.Blocks["a1"].Hash!);
        await using PbtMigrationImport import = new(_ => Task.CompletedTask, Substitute.For<IProcessExitSource>(), LimboLogs.Instance);
        await using MerkleShadowFollower merkle = harness.CreateMerkleShadowFollower();
        MigrationTelemetry telemetry = CreateTelemetry(harness, import, merkle);

        harness.Follower.Start();

        MigrationProgressForRpc stalled = new("running", new MigrationDirectionForRpc("stalled", 0, harness.Blocks["anchor"].Hash!, Eip8347FixtureState.PbtRoot("anchor"), StalledError), null);
        Assert.That(() => telemetry.GetProgress(), Is.EqualTo(stalled).After(10_000, 50));
        AssertFixture("stalled", stalled);
    }

    private static IEnumerable<TestCaseData> ImportOutcomes()
    {
        yield return new TestCaseData(null, null).SetName("Import_succeeds");
        yield return new TestCaseData(new InvalidDataException("corrupt image"), ExitCodes.GeneralError).SetName("Invalid_image_exits");
        yield return new TestCaseData(new InvalidConfigurationException("corrupt image", ExitCodes.ConflictingConfigurations), ExitCodes.ConflictingConfigurations).SetName("Invalid_configuration_exits_with_its_code");
        yield return new TestCaseData(new IOException("corrupt image"), null).SetName("Other_failure_keeps_running");
    }

    [TestCaseSource(nameof(ImportOutcomes))]
    public async Task Background_import_reports_importing_then_its_outcome(Exception? failure, int? exitCode)
    {
        using MigrationBalFollowerTests.Harness harness = new();
        harness.SelectHead("a1");
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IProcessExitSource exitSource = Substitute.For<IProcessExitSource>();
        await using PbtMigrationImport import = new(async _ =>
        {
            await release.Task;
            if (failure is not null) throw failure;
        }, exitSource, LimboLogs.Instance);
        await using MerkleShadowFollower merkle = harness.CreateMerkleShadowFollower();
        MigrationTelemetry telemetry = CreateTelemetry(harness, import, merkle);

        import.Start();
        Assert.That(telemetry.GetProgress(), Is.EqualTo(new MigrationProgressForRpc("importing", null, null)));
        release.SetResult();
        Assert.That(() => import.IsPending, Is.False.After(10_000, 50));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(import.Error, failure is null ? Is.Null : Is.EqualTo("corrupt image"));
            Assert.That(telemetry.GetProgress(), Is.EqualTo(new MigrationProgressForRpc(failure is null ? "running" : "importFailed", null, null)));
        }
        if (exitCode is null) exitSource.DidNotReceiveWithAnyArgs().Exit(default);
        else exitSource.Received(1).Exit(exitCode.Value);
    }

    private const string StalledError = "BAL range unavailable or canonical ancestry changed.";

    private static MigrationTelemetry CreateTelemetry(MigrationBalFollowerTests.Harness harness, PbtMigrationImport import, MerkleShadowFollower merkle) =>
        new(harness.BlockTree, harness.Manager, import, harness.Follower, merkle, harness.SpecProvider);

    private static void AssertFixture(string name, MigrationProgressForRpc progress)
    {
        using JsonDocument fixtures = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Eip8347FixtureState.Directory, "migration-rpc.json")));
        using JsonDocument actual = JsonDocument.Parse(new EthereumJsonSerializer().Serialize(progress));
        Assert.That(JsonElement.DeepEquals(actual.RootElement, fixtures.RootElement.GetProperty(name)), Is.True, name);
    }
}
