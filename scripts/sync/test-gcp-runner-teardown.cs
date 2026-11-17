#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include sync-tests.helpers.cs
#:include lifecycle-tests.helpers.cs
#:include ../../.github/actions/gcp-runner/gcp.helpers.cs
#:include ../../.github/actions/gcp-runner/lifecycle.helpers.cs

foreach (var (input, expected) in new[] { ("", "0"), ("not-a-timestamp", "0"), ("2026-09-08T21:47:04.630-07:00", "1788929224") })
    await Cases.Test($"preemption timestamp {input}", async () =>
    {
        using var fixture = new LifecycleFixture { IsDestroy = true, InsertTime = input };
        Cases.Equal(0, await fixture.Execute());
        Cases.Equal("true", fixture.Outputs["preempted"]);
        Cases.Equal(expected, fixture.Outputs["preempted_at"]);
    });
foreach (var scenario in new[] { "lookup-failed", "already-deleted", "deleted-by-action", "unknown", "terminated", "creation-failed", "delete-failed" })
    await Cases.Test($"teardown {scenario}", async () =>
    {
        using var fixture = new LifecycleFixture { IsDestroy = true, Operations = "" };
        if (scenario == "lookup-failed") fixture.LookupStatus = 1;
        if (scenario == "already-deleted") fixture.Operations = "delete";
        if (scenario == "terminated") fixture.Operations = "compute.instances.terminateOnHostMaintenance";
        if (scenario == "creation-failed") fixture.Environment["EXPECT_INSTANCE"] = "false";
        if (scenario is "deleted-by-action" or "delete-failed") fixture.InstanceZone = "europe-west1-b";
        if (scenario == "delete-failed") { fixture.DeleteStatus = 1; fixture.DeleteError = "Permission denied deleting instance\n"; }
        Cases.Equal(scenario is "lookup-failed" or "delete-failed" ? 1 : 0, await fixture.Execute());
        Cases.Equal(scenario, fixture.Outputs["terminated_by"]);
        if (scenario == "delete-failed") Cases.True(fixture.Transcript.Contains(fixture.DeleteError, StringComparison.Ordinal), "gcloud deletion error retained");
        Cases.Equal("false", fixture.Outputs["preempted"]);
        Cases.Equal("0", fixture.Outputs["preempted_at"]);
        if (scenario is "unknown" or "terminated") Cases.True(fixture.Transcript.Contains("::warning title=GCP runner::", StringComparison.Ordinal), "missing-instance warning");
        if (scenario == "unknown") Cases.True(fixture.Transcript.Contains("vanished with no operations recorded", StringComparison.Ordinal), "unknown diagnostic");
        if (scenario == "terminated") Cases.True(fixture.Transcript.Contains("was terminated externally", StringComparison.Ordinal), "external termination diagnostic");
        if (scenario == "creation-failed") Cases.True(fixture.Transcript.Contains("creation did not complete", StringComparison.Ordinal), "creation failure diagnostic");
    });
await Cases.Test("concurrent deletion and leftover registration diagnostics", async () =>
{
    using var fixture = new LifecycleFixture { IsDestroy = true };
    var lookups = 0;
    fixture.Handler = (command, arguments) => command == "gh"
        ? new CommandResult(0, arguments.Contains("DELETE") ? "" : "42\n")
        : arguments.Contains("list") ? new CommandResult(0, ++lookups == 1 ? "europe-west1-b" : "") : new CommandResult(1);
    Cases.Equal(0, await fixture.Execute());
    Cases.Equal("deleted-concurrently", fixture.Outputs["terminated_by"]);
    Cases.True(fixture.Transcript.Contains("was already being deleted and is now gone", StringComparison.Ordinal), "concurrent deletion diagnostic");
    Cases.True(fixture.Transcript.Contains("removing leftover runner registration 42", StringComparison.Ordinal), "registration cleanup diagnostic");
});
Cases.Finish();
