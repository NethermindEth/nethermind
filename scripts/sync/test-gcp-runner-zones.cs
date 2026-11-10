#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include sync-tests.helpers.cs
#:include lifecycle-tests.helpers.cs
#:include ../../.github/actions/gcp-runner/gcp.helpers.cs
#:include ../../.github/actions/gcp-runner/lifecycle.helpers.cs

using System.Text.RegularExpressions;

var zones = string.Join(',', new[] { "europe-west1", "europe-west4", "europe-north1", "europe-west3", "europe-west2", "europe-west6" }.SelectMany(region => (region == "europe-west1" ? "bcd" : "abc").Select(suffix => $"{region}-{suffix}")));
foreach (var seed in new[] { "seed", "mainnet", "gnosis", "hoodi", "sepolia" })
    Cases.Test($"zone permutation deterministic contiguous {seed}", () =>
    {
        var ordered = Gcp.OrderZones(seed, zones);
        Cases.Equal(string.Join(',', zones.Split(',').Order()), string.Join(',', ordered.Order()));
        Cases.Equal(string.Join(',', ordered), string.Join(',', Gcp.OrderZones(seed, zones)));
        var collapsed = ordered.Select(Gcp.Region).Where((region, index) => index == 0 || region != Gcp.Region(ordered[index - 1])).ToArray();
        Cases.Equal(collapsed.Length, collapsed.Distinct().Count());
    });
Cases.Test("concurrent runners distribute regions", () => Cases.True(new[] { "gh-f-1-master-mainnet", "gh-f-1-master-gnosis", "gh-hp-1-master-mainnet", "gh-hp-1-master-gnosis" }.Select(seed => Gcp.Region(Gcp.OrderZones(seed, zones)[0])).Distinct().Count() > 1, "region distribution"));
foreach (var input in new[] { "europe-west1-b,europe-west1-c,europe-west1-d", "europe-west1-b", " europe-west1-b , ,europe-west4-a ", "" })
    Cases.Test($"zone edge case {input}", () => Cases.Equal(string.Join(',', Gcp.Zones(input).Order()), string.Join(',', Gcp.OrderZones("seed", input).Order())));
foreach (var (message, expected) in new[] { ("Quota 'LOCAL_SSD_TOTAL_GB' exceeded. Limit: 15000.0 in region europe-west1.", "quota"), ("Quota 'CPUS_ALL_REGIONS' exceeded globally.", "fatal"), ("Constraint QUOTA_EXCEEDED violated", "fatal"), ("zone does not have enough resources available", "retryable"), ("ZONE_RESOURCE_POOL_EXHAUSTED", "retryable"), ("PERMISSION_DENIED: Required 'compute.instances.create' permission", "fatal"), ("Machine type with name 'c3d' does not exist in zone 'z'", "not-offered"), ("Disk type with name 'hyperdisk' does not exist in zone 'z'", "unrecognised"), ("something entirely new", "unrecognised"), ("Machine type with name x does not exist in zone z QUOTA_EXCEEDED", "not-offered") })
    Cases.Test($"classify {message}", () => Cases.Equal(expected, Gcp.Classify(message)));
foreach (var scenario in new[] { "quota", "capacity", "not-offered", "nowhere", "spot-quota", "spot-capacity", "fallback-exhausted", "fallback-success", "standard", "spot", "global", "rotate", "timeout", "gone" })
    await Cases.Test($"create {scenario}", async () =>
    {
        using var fixture = new LifecycleFixture();
        fixture.Environment["ZONES"] = "europe-west1-b,europe-west1-c,europe-west4-a";
        fixture.SucceedZone = "europe-west4-a";
        if (scenario is "capacity" or "spot-capacity" or "fallback-exhausted" or "fallback-success" or "spot") fixture.Capacity = fixture.Environment["ZONES"];
        if (scenario == "not-offered") fixture.NotOffered = "europe-west1-b";
        if (scenario == "nowhere") fixture.NotOffered = fixture.Environment["ZONES"];
        if (scenario.StartsWith("spot", StringComparison.Ordinal) || scenario.StartsWith("fallback", StringComparison.Ordinal) || scenario == "nowhere") fixture.Environment["PROVISIONING_MODEL"] = "SPOT";
        if (scenario.StartsWith("fallback", StringComparison.Ordinal) || scenario == "nowhere") fixture.Environment["SPOT_FALLBACK_TO_STANDARD"] = "true";
        if (scenario is "nowhere" or "spot-quota" or "spot-capacity" or "fallback-exhausted" or "global" or "rotate") fixture.SucceedZone = "";
        if (scenario == "fallback-success") fixture.SucceedModel = "STANDARD";
        if (scenario == "global") fixture.GlobalQuota = true;
        if (scenario == "rotate") fixture.Environment["ZONE_ORDER"] = "rotate";
        if (scenario == "timeout") fixture.OnlineStatus = "offline";
        if (scenario == "gone") fixture.HttpStatus = 404;
        var code = await fixture.Execute();
        Cases.Equal(new[] { "quota", "capacity", "not-offered", "fallback-success", "standard", "spot" }.Contains(scenario) ? 0 : 1, code);
        Cases.True(!File.Exists(Path.Combine(fixture.Directory, "jitconfig.b64")), "JIT file removed");
        var attempts = string.Join(',', fixture.Attempts);
        if (scenario is "quota" or "spot-quota" or "standard") Cases.Equal("europe-west1-b,europe-west4-a", attempts);
        if (scenario is "capacity" or "spot-capacity" or "nowhere" or "spot") Cases.Equal(fixture.Environment["ZONES"], attempts);
        if (scenario == "not-offered") Cases.Equal(fixture.Environment["ZONES"], attempts);
        if (scenario is "not-offered" or "quota" or "capacity")
        {
            var notice = scenario == "not-offered" ? "does not offer" : scenario == "quota" ? "at quota" : "no capacity, trying next";
            Cases.True(fixture.Transcript.Contains("::notice title=GCP runner::", StringComparison.Ordinal) && fixture.Transcript.Contains(notice, StringComparison.Ordinal), "zone retry diagnostic");
        }
        if (scenario == "global") Cases.Equal("europe-west1-b", attempts);
        if (scenario == "rotate") Cases.True(fixture.Attempts[0] != "europe-west1-b", "rotated first zone");
        if (scenario == "fallback-exhausted") Cases.Equal(fixture.Environment["ZONES"] + "," + fixture.Environment["ZONES"], attempts);
        if (scenario == "nowhere") Cases.Equal("SPOT", string.Join(',', fixture.Models.Distinct()));
        Cases.Equal(scenario == "spot-capacity", fixture.Outputs.ContainsKey("spot_exhausted"));
        if (scenario == "spot-capacity") Cases.True(long.Parse(fixture.Outputs["spot_exhausted_at"]) > 0, "exhaustion timestamp");
        if (code == 0)
        {
            Cases.Equal("1.2.3.4", fixture.Outputs["instance_ip"]);
            Cases.True(fixture.Summary.Contains($"{fixture.Models[^1]} in europe-west4-a", StringComparison.Ordinal), "summary model");
            if (scenario == "spot") Cases.True(!fixture.Transcript.Contains("::warning", StringComparison.Ordinal), "SPOT success has no warning");
            else
            {
                Cases.True(fixture.Transcript.Contains("::warning title=GCP runner::", StringComparison.Ordinal), "STANDARD warning");
                Cases.True(fixture.Transcript.Contains(scenario == "fallback-success" ? "fell back to STANDARD" : "requested explicitly", StringComparison.Ordinal), "STANDARD requested/fallback distinction");
            }
        }
        else Cases.True(fixture.Commands.Any(command => command[0] == "gh" && command.Contains("DELETE")), "runner cleanup");
    });
Cases.Test("no implicit standard fallback", () =>
{
    var text = File.ReadAllText(Path.Combine(Cases.Root(), ".github/actions/gcp-runner/action.yaml"));
    var block = text[text.IndexOf("  spot_fallback_to_standard:", StringComparison.Ordinal)..];
    Cases.True(SpotFallbackRegex().Match(block).Value.Contains("default: \"false\"", StringComparison.Ordinal), "default false");
});
foreach (var response in new[] { "{broken", "[]", "null", "{\"status\":\"offline\"}" })
    await Cases.Test($"poll past invalid or offline runner response {response}", async () =>
    {
        using var fixture = new LifecycleFixture { SucceedZone = "europe-west1-b" };
        fixture.Environment["BOOT_TIMEOUT"] = "5";
        fixture.RunnerResponses.Enqueue(response);
        Cases.Equal(0, await fixture.Execute());
        Cases.Equal(2, fixture.Commands.Count(command => command[0] == "curl"));
        Cases.True(!fixture.Commands.Any(command => command.Contains("delete") || command.Contains("DELETE")), "retry leaves live runner intact");
    });
Cases.Finish();

partial class Program
{
    [GeneratedRegex(@"\A  spot_fallback_to_standard:\n(?:    .*\n)*")]
    private static partial Regex SpotFallbackRegex();
}
