#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include BenchmarkProcess.Helpers.cs

using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

using var cancellation = new CancellationTokenSource();
using var termination = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); });
ConsoleCancelEventHandler cancel = (_, context) => { context.Cancel = true; cancellation.Cancel(); };
Console.CancelKeyPress += cancel;
try
{
    var state = BenchmarkProcess.EnvironmentValue("STATE_DIR");
    if (state.Length == 0) throw new ArgumentException("STATE_DIR: directory for the saved sysfs values");
    var cpu = BenchmarkProcess.EnvironmentValue("CPU_SYSFS", "/sys/devices/system/cpu");
    var saved = Path.Combine(state, "cpu-sysfs.orig");
    var maximum = BenchmarkProcess.EnvironmentValue("CPU_MAX_FREQ_KHZ");
    if (args.Length != 1 || args[0] is not ("apply" or "restore")) throw new ArgumentException("usage: CpuStabilize.cs apply|restore");
    if (args[0] == "restore") { await Restore(); return 0; }
    if (maximum.Length > 0 && !PositiveIntegerRegex().IsMatch(maximum)) throw new ArgumentException($"CPU_MAX_FREQ_KHZ must be a positive integer, got '{maximum}'");
    Directory.CreateDirectory(state);
    if (HasState())
    {
        BenchmarkProcess.Log("::warning::stale saved cpu state from an earlier run — restoring it first");
        await Restore();
    }
    if (HasState()) throw new IOException($"refusing to apply over unrestored cpu state in {saved} — the box is still capped; restore it by hand first");
    var turbo = File.Exists(Path.Combine(cpu, "intel_pstate/no_turbo")) ? (Path.Combine(cpu, "intel_pstate/no_turbo"), "1") : File.Exists(Path.Combine(cpu, "cpufreq/boost")) ? (Path.Combine(cpu, "cpufreq/boost"), "0") : ("", "");
    var policyRoot = Path.Combine(cpu, "cpufreq");
    var policies = Directory.Exists(policyRoot) ? Directory.EnumerateDirectories(policyRoot, "policy*").Where(path => PolicyRegex().IsMatch(Path.GetFileName(path)) && File.Exists(Path.Combine(path, "scaling_governor"))).Order(StringComparer.Ordinal).ToArray() : [];
    var originals = new List<(string Path, string Value)>();
    if (turbo.Item1.Length > 0) Save(turbo.Item1);
    foreach (var policy in policies)
    {
        Save(Path.Combine(policy, "scaling_governor"));
        if (maximum.Length > 0) Save(Path.Combine(policy, "scaling_max_freq"));
    }
    await File.WriteAllTextAsync(saved, string.Concat(originals.Select(value => $"{value.Path}\t{value.Value}\n")), cancellation.Token);
    if (turbo.Item1.Length > 0)
        BenchmarkProcess.Log(await Write(turbo.Item1, turbo.Item2) ? $"turbo boost disabled ({turbo.Item1}={turbo.Item2})" : $"::warning::could not write {turbo.Item1}");
    if (policies.Length == 0)
    {
        BenchmarkProcess.Log("no cpufreq sysfs on this host — CPU frequency left as is");
        if (maximum.Length > 0) BenchmarkProcess.Log("::warning::CPU_MAX_FREQ_KHZ set but no scaling_max_freq sysfs on this host");
        return 0;
    }
    var changed = 0;
    foreach (var policy in policies) if (await Write(Path.Combine(policy, "scaling_governor"), "performance")) changed++;
    BenchmarkProcess.Log($"governor=performance on {changed} of {policies.Length} cpufreq policies");
    if (maximum.Length > 0)
    {
        changed = 0;
        foreach (var policy in policies) if (await Write(Path.Combine(policy, "scaling_max_freq"), maximum)) changed++;
        BenchmarkProcess.Log($"scaling_max_freq={maximum} kHz on {changed} of {policies.Length} cpufreq policies (now {File.ReadAllText(Path.Combine(policies[0], "scaling_max_freq")).Trim()})");
    }
    return 0;

    bool HasState() => File.Exists(saved) && new FileInfo(saved).Length > 0;
    void Save(string path) => originals.Add((path, File.ReadAllText(path).TrimEnd('\r', '\n')));
    async Task<bool> Write(string path, string value)
    {
        try { await File.WriteAllTextAsync(path, value, cancellation.Token); return true; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var result = await BenchmarkProcess.RootAsync("tee", [path], cancellation.Token, value);
            return result.Code == 0;
        }
    }
    async Task Restore()
    {
        if (!HasState()) { BenchmarkProcess.Log("nothing to restore"); return; }
        var total = 0; var count = 0;
        foreach (var line in File.ReadLines(saved))
        {
            var pair = line.Split('\t', 2);
            if (pair.Length != 2 || pair[0].Length == 0 || pair[1].Length == 0) continue;
            total++;
            if (await Write(pair[0], pair[1])) count++;
        }
        BenchmarkProcess.Log($"restored {count} of {total} cpu sysfs value(s)");
        if (count == total) File.Delete(saved);
        else BenchmarkProcess.Log($"::warning::keeping {saved} - {total - count} value(s) could not be restored");
    }
}
catch (OperationCanceledException) { Console.Error.WriteLine("CPU stabilization cancelled; saved originals retained."); return 1; }
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
{
    Console.Error.WriteLine($"ERROR: {exception.Message}"); return 1;
}
finally { Console.CancelKeyPress -= cancel; }

partial class Program
{
    [GeneratedRegex("^[1-9][0-9]*$")]
    private static partial Regex PositiveIntegerRegex();
    [GeneratedRegex("^policy[0-9]")]
    private static partial Regex PolicyRegex();
}
