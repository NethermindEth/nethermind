#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include RpcScriptTests.Helpers.cs

using var tests = new RpcScriptTests();
var cpu = Path.Combine(tests.DirectoryPath, "cpu");
var state = Path.Combine(tests.DirectoryPath, "state");
var policy = Path.Combine(cpu, "cpufreq", "policy0");
var turbo = Path.Combine(cpu, "intel_pstate", "no_turbo");
Directory.CreateDirectory(policy);
Directory.CreateDirectory(Path.GetDirectoryName(turbo)!);
var governor = Path.Combine(policy, "scaling_governor");
var maximum = Path.Combine(policy, "scaling_max_freq");
File.WriteAllText(turbo, "0"); File.WriteAllText(governor, "powersave"); File.WriteAllText(maximum, "4000000");
var previous = new[] { "CPU_SYSFS", "STATE_DIR", "CPU_MAX_FREQ_KHZ" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
try
{
    Environment.SetEnvironmentVariable("CPU_SYSFS", cpu); Environment.SetEnvironmentVariable("STATE_DIR", state); Environment.SetEnvironmentVariable("CPU_MAX_FREQ_KHZ", "2000000");
    tests.Case("apply records originals before changing policy", () =>
    {
        var result = RpcScriptTests.Run("scripts/rpc-bench/CpuStabilize.cs", "apply");
        var saved = File.ReadAllText(Path.Combine(state, "cpu-sysfs.orig"));
        RpcScriptTests.Assert(result.Code == 0 && File.ReadAllText(turbo) == "1" && File.ReadAllText(governor) == "performance" && File.ReadAllText(maximum) == "2000000" && saved.Contains(governor + "\tpowersave\n") && saved.Contains(maximum + "\t4000000\n"), result.ToString());
    });
    tests.Case("stale state is restored before a second apply", () =>
    {
        var result = RpcScriptTests.Run("scripts/rpc-bench/CpuStabilize.cs", "apply");
        var saved = File.ReadAllText(Path.Combine(state, "cpu-sysfs.orig"));
        RpcScriptTests.Assert(result.Code == 0 && result.Output.Contains("stale saved cpu state") && saved.Contains(governor + "\tpowersave\n") && saved.Contains(maximum + "\t4000000\n"), result.ToString());
    });
    tests.Case("restore replays originals and removes completed record", () =>
    {
        var result = RpcScriptTests.Run("scripts/rpc-bench/CpuStabilize.cs", "restore");
        RpcScriptTests.Assert(result.Code == 0 && File.ReadAllText(turbo) == "0" && File.ReadAllText(governor) == "powersave" && File.ReadAllText(maximum) == "4000000" && !File.Exists(Path.Combine(state, "cpu-sysfs.orig")), result.ToString());
    });
    foreach (var invalid in new[] { "0", "-1", "1.5", "1;rm", "abc" })
        tests.Case($"reject invalid cap {invalid}", () =>
        {
            Environment.SetEnvironmentVariable("CPU_MAX_FREQ_KHZ", invalid);
            var result = RpcScriptTests.Run("scripts/rpc-bench/CpuStabilize.cs", "apply");
            RpcScriptTests.Assert(result.Code == 1 && result.Error.Contains("positive integer") && File.ReadAllText(governor) == "powersave", result.ToString());
        });
    tests.Case("hosts without cpufreq are skipped", () =>
    {
        Environment.SetEnvironmentVariable("CPU_SYSFS", Path.Combine(tests.DirectoryPath, "missing"));
        Environment.SetEnvironmentVariable("CPU_MAX_FREQ_KHZ", "2000000");
        var result = RpcScriptTests.Run("scripts/rpc-bench/CpuStabilize.cs", "apply");
        RpcScriptTests.Assert(result.Code == 0 && result.Output.Contains("no cpufreq sysfs") && result.Output.Contains("CPU_MAX_FREQ_KHZ set"), result.ToString());
    });
}
finally { foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value); }
tests.Complete();
