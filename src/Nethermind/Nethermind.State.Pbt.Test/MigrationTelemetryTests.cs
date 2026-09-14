// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.Serialization.Json;
using Nethermind.State.Pbt.Migration;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class MigrationTelemetryTests
{
    [Test]
    public async Task Progress_and_shadow_roots_follow_the_native_pbt_state_and_finality()
    {
        using MigrationBalFollowerTests.Harness harness = new();
        await using PbtBalFollowerScheduler scheduler = new((_, _) => Task.FromResult(true), () => harness.Follower.Error, () => harness.Follower.Cursor);
        MigrationTelemetry telemetry = new(harness.BlockTree, harness.Manager, scheduler, harness.SpecProvider);
        Assert.That(telemetry.GetProgress(), Is.EqualTo(new MigrationProgressForRpc("running", null, null)));
        Assert.That(telemetry.GetShadowRoot(harness.Blocks["anchor"].Hash!), Is.Null, "no state before the anchor import");
        await harness.Publish();
        Assert.That(telemetry.GetShadowRoot(harness.Blocks["anchor"].Hash!), Is.EqualTo(Root("anchor")));
        Assert.That(await harness.Follower.Follow(harness.Blocks["a3"].Header, default), Is.True, harness.Follower.Error);

        MigrationProgressForRpc following = telemetry.GetProgress();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(following.Phase, Is.EqualTo("running"));
            Assert.That(following.Binary, Is.EqualTo(new MigrationDirectionForRpc("following", 3, harness.Blocks["a3"].Hash!, Root("a3"), "")));
            Assert.That(following.Merkle, Is.Null);
            Assert.That(telemetry.GetShadowRoot(harness.Blocks["a2"].Hash!), Is.EqualTo(Root("a2")));
            Assert.That(telemetry.GetShadowRoot(harness.Blocks["a4"].Hash!), Is.Null, "post-activation blocks have no shadow");
            Assert.That(telemetry.GetShadowRoot(harness.Blocks["a5"].Hash!), Is.Null, "known header without retained state");
            Assert.That(telemetry.GetShadowRoot(Hash256.Zero), Is.Null);
        }

        harness.BlockTree.TryUpdateMainChain(harness.Blocks["a3"].Header, true, true, [harness.Blocks["a3"]]);
        MigrationProgressForRpc synced = telemetry.GetProgress();
        Assert.That(synced.Binary!.Phase, Is.EqualTo("synced"));
        AssertFixture("prefork", synced);

        harness.BlockTree.TryUpdateMainChain(harness.Blocks["a4"].Header, true, true, [harness.Blocks["a4"]]);
        harness.BlockTree.ForkChoiceUpdated(harness.Blocks["a4"].Hash!, Hash256.Zero);
        Assert.That(telemetry.GetProgress(), Is.EqualTo(new MigrationProgressForRpc("done", null, null)));
        AssertFixture("done", telemetry.GetProgress());

        Hash256 Root(string name) => new(harness.Metadata[name].GetProperty("pbtRoot").GetString()!);
    }

    [Test]
    public async Task Lagging_progress_reports_stall_without_inventing_cursor_or_root()
    {
        using MigrationBalFollowerTests.Harness harness = new();
        await harness.Publish();
        TaskCompletionSource attempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using PbtBalFollowerScheduler scheduler = new((_, _) => { attempted.TrySetResult(); return Task.FromResult(false); }, () => null,
            () => new PbtFollowerCursor(0, harness.Blocks["anchor"].Hash!, harness.TreeRoot(harness.Blocks["anchor"].Header)));
        MigrationTelemetry telemetry = new(harness.BlockTree, harness.Manager, scheduler, harness.SpecProvider);
        Assert.That(telemetry.GetProgress().Binary!.Phase, Is.EqualTo("following"));
        scheduler.Schedule(harness.Blocks["a1"].Header);
        await attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(() => scheduler.Error, Is.Not.Null.After(5000, 10));
        MigrationProgressForRpc stalled = telemetry.GetProgress();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stalled.Binary!.Phase, Is.EqualTo("stalled"));
            Assert.That(stalled.Binary.CursorHash, Is.EqualTo(harness.Blocks["anchor"].Hash));
            Assert.That(stalled.Binary.ShadowRoot, Is.EqualTo(new Hash256(harness.Metadata["anchor"].GetProperty("pbtRoot").GetString()!)));
            Assert.That(stalled.Binary.Error, Is.Not.Empty);
            Assert.That(stalled.Merkle, Is.Null);
        }
        AssertFixture("stalled", stalled);
    }

    private static void AssertFixture(string name, MigrationProgressForRpc progress)
    {
        using JsonDocument fixtures = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(TestContext.CurrentContext.TestDirectory,
            "Fixtures", "Eip8347", "migration-rpc.json")));
        using JsonDocument actual = JsonDocument.Parse(new EthereumJsonSerializer().Serialize(progress));
        Assert.That(JsonElement.DeepEquals(actual.RootElement, fixtures.RootElement.GetProperty(name)), Is.True, name);
        AssertWire(progress);
    }

    private static void AssertWire(MigrationProgressForRpc progress)
    {
        string serialized = new EthereumJsonSerializer().Serialize(progress);
        TestContext.Out.WriteLine($"Migration RPC fixture: {serialized}");
        using JsonDocument json = JsonDocument.Parse(serialized);
        Assert.That(json.RootElement.GetProperty("phase").GetString(), Is.EqualTo(progress.Phase));
        foreach (string direction in new[] { "binary", "merkle" })
        {
            JsonElement value = json.RootElement.GetProperty(direction);
            if (value.ValueKind == JsonValueKind.Null) continue;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(value.GetProperty("cursor").ValueKind, Is.EqualTo(JsonValueKind.Number));
                Assert.That(value.GetProperty("cursorHash").GetString(), Has.Length.EqualTo(66));
                Assert.That(value.GetProperty("shadowRoot").GetString(), Has.Length.EqualTo(66));
                Assert.That(value.GetProperty("error").ValueKind, Is.EqualTo(JsonValueKind.String));
            }
        }
    }
}
