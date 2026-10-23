#!/usr/bin/env dotnet
#:include SyncTests.Helpers.cs
#:include LifecycleTests.Helpers.cs
#:include ../../.github/actions/gcp-runner/Gcp.Helpers.cs
#:include ../../.github/actions/gcp-runner/Lifecycle.Helpers.cs

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
        if (scenario == "delete-failed") fixture.DeleteStatus = 1;
        Cases.Equal(scenario is "lookup-failed" or "delete-failed" ? 1 : 0, await fixture.Execute());
        Cases.Equal(scenario, fixture.Outputs["terminated_by"]);
        Cases.Equal("false", fixture.Outputs["preempted"]);
        Cases.Equal("0", fixture.Outputs["preempted_at"]);
    });
Cases.Finish();
