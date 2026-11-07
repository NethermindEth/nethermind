// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Diagnostics.NETCore.Client;

namespace Nethermind.Tools.ProfilingMcp.Native;

/// <summary>Samples every thread of a Linux process, native code included, with <c>perf</c>, and saves the stacks in folded form.</summary>
/// <remarks>
/// perf samples the <c>cpu-clock</c> software event, which works in VMs without hardware counters, at <see cref="Hz"/>, so one sample is one
/// millisecond of CPU time; threads that are blocked are not sampled. Stacks are walked by frame pointers: JIT-compiled .NET code keeps them,
/// native libraries built without them show shortened stacks. For a .NET target the runtime is asked over its diagnostics port to write a perf map
/// for the duration, which names JIT-compiled methods; no restart is needed.
/// </remarks>
public static class NativeSampler
{
    public const int Hz = 1000;
    public const string Extension = ".folded";

    public static bool IsFolded(string path) => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Records <paramref name="pid"/> for <paramref name="duration"/> and writes its stacks to <paramref name="output"/>.</summary>
    /// <returns>Where the runtime's perf map was read and how many JIT-compiled methods it named; null for a process without a .NET diagnostics port.</returns>
    public static async Task<(string PerfMap, int Methods)?> CollectAsync(int pid, TimeSpan duration, string output, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("native_sample needs perf, which runs on Linux only; profile a container or a Linux host.");

        string data = output + ".perf.data";
        DiagnosticsClient? runtime = EnablePerfMap(pid);
        try
        {
            // Build ids are skipped: perf script reads each library by path, from the target's mount namespace.
            await RunPerfAsync(["record", "--quiet", "--no-buildid", "--no-buildid-cache", "-e", "cpu-clock", "-F", Hz.ToString(CultureInfo.InvariantCulture), "-g", "-p", pid.ToString(CultureInfo.InvariantCulture),
                "-o", data, "--", "sleep", duration.TotalSeconds.ToString(CultureInfo.InvariantCulture)], stdout: null, cancellationToken);

            JitMap jit = runtime is null ? JitMap.Empty : JitMap.Load(PerfMapPath(pid));
            PerfScriptFolder folder = new(jit);
            await RunPerfAsync(["script", "--input", data, "-F", "comm,tid,ip,sym,dso"], folder.Add, cancellationToken);
            await File.WriteAllLinesAsync(output, folder.Fold(), cancellationToken);
            return runtime is null ? null : (PerfMapPath(pid), jit.Count);
        }
        finally
        {
            File.Delete(data);
            DisablePerfMap(runtime, pid);
        }
    }

    private static DiagnosticsClient? EnablePerfMap(int pid)
    {
        try
        {
            DiagnosticsClient client = new(pid);
            client.EnablePerfMap(PerfMapType.PerfMap);
            return client;
        }
        catch (Exception e) when (e is ServerNotAvailableException or ServerErrorException or UnsupportedCommandException or TimeoutException)
        {
            // Not a .NET process, or one whose runtime predates the command: its JIT-compiled frames stay unnamed.
            return null;
        }
    }

    private static void DisablePerfMap(DiagnosticsClient? runtime, int pid)
    {
        if (runtime is null)
            return;

        try
        {
            runtime.DisablePerfMap();
        }
        catch (Exception e) when (e is ServerNotAvailableException or ServerErrorException or TimeoutException)
        {
            // The process exited.
        }

        // The runtime would otherwise leave them in the target's temp folder; it writes them afresh when enabled again.
        File.Delete(PerfMapPath(pid));
        File.Delete(Path.Combine(Path.GetDirectoryName(PerfMapPath(pid))!, $"perfinfo-{pid}.map"));
    }

    /// <summary>Where the runtime writes the perf map: its temp folder, seen through the process's root.</summary>
    private static string PerfMapPath(int pid)
    {
        string temp = "/tmp";
        try
        {
            foreach (string variable in File.ReadAllText($"/proc/{pid}/environ").Split('\0'))
            {
                if (variable.StartsWith("TMPDIR=", StringComparison.Ordinal) && variable.Length > "TMPDIR=".Length)
                    temp = variable["TMPDIR=".Length..].TrimEnd('/');
            }
        }
        catch (IOException)
        {
        }

        return $"/proc/{pid}/root{temp}/perf-{pid}.map";
    }

    private static async Task RunPerfAsync(string[] arguments, Action<string>? stdout, CancellationToken cancellationToken)
    {
        ProcessStartInfo info = new("perf", arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        Process process;
        try
        {
            process = Process.Start(info)!;
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException("perf is not installed. A sidecar image has it unless it was built on a base image without apt; on a Linux host install linux-perf or linux-tools for the running kernel.");
        }

        using (process)
        {
            await using CancellationTokenRegistration kill = cancellationToken.Register(() =>
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // It has already exited.
                }
            });
            Task<string> error = process.StandardError.ReadToEndAsync(CancellationToken.None);
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
                stdout?.Invoke(line);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"perf {arguments[0]} failed with exit code {process.ExitCode}: {(await error).Trim()}");
        }
    }
}
