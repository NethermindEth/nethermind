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

    /// <summary>How long to wait for another session's sample of the same process.</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(15);

    /// <summary>How many times <c>perf record</c> is started before its failure is reported; see <see cref="RecordAsync"/>.</summary>
    internal const int RecordAttempts = 4;

    /// <summary>Records <paramref name="pid"/> for <paramref name="duration"/> and writes its stacks to <paramref name="output"/>.</summary>
    /// <returns>
    /// Where the runtime's perf map was read, how many JIT-compiled methods it named, and whether a map this sample turned on was left on;
    /// null for a process without a .NET diagnostics port.
    /// </returns>
    /// <remarks>
    /// Samples of one process are serialized across sessions, since each turns the perf map on and off. A map that was on before, because the
    /// process was started with <c>DOTNET_PerfMapEnabled</c> or someone else enabled it, is left on and its files are kept. So is a map turned on
    /// for a process that writes jit dumps, since turning the map off ends those too.
    /// </remarks>
    public static async Task<(string PerfMap, int Methods, bool LeftOn)?> CollectAsync(int pid, TimeSpan duration, string output, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("native_sample needs perf, which runs on Linux only; profile a container or a Linux host.");

        using IDisposable exclusive = await ProcessLock.AcquireAsync("perf", pid, LockTimeout, cancellationToken);
        string data = output + ".perf.data";
        IReadOnlyDictionary<string, string> environment = ReadEnvironment(pid);
        string perfMap = PerfMapPath(pid, environment);
        DateTime started = StartTime(pid);
        bool mapWritten = WrittenSince(perfMap, started);
        DiagnosticsClient? runtime = null;
        if (!PerfMapAlreadyOn(environment, mapWritten))
        {
            // A map an earlier process left must not lend its addresses to this one's.
            File.Delete(perfMap);
            runtime = EnablePerfMap(pid);
        }

        bool isDotNet = runtime is not null || mapWritten;
        bool leaveOn = runtime is not null && WritesJitDumps(environment, WrittenSince(Path.Combine(Path.GetDirectoryName(perfMap)!, $"jit-{pid}.dump"), started));
        try
        {
            // Build ids are skipped: perf script reads each library by path, from the target's mount namespace.
            await RecordAsync(() => RunPerfAsync(["record", "--quiet", "--no-buildid", "--no-buildid-cache", "-e", "cpu-clock", "-F", Hz.ToString(CultureInfo.InvariantCulture), "-g",
                "-p", pid.ToString(CultureInfo.InvariantCulture), "-o", data, "--", "sleep", duration.TotalSeconds.ToString(CultureInfo.InvariantCulture)], stdout: null, cancellationToken),
                duration, TimeSpan.FromSeconds(1), cancellationToken);

            JitMap jit = isDotNet ? JitMap.Load(perfMap) : JitMap.Empty;
            PerfScriptFolder folder = new(jit);
            await RunPerfAsync(["script", "--input", data, "-F", "comm,tid,ip,sym,dso"], folder.Add, cancellationToken);
            await File.WriteAllLinesAsync(output, folder.Fold(), cancellationToken);
            return isDotNet ? (perfMap, jit.Count, leaveOn) : null;
        }
        finally
        {
            try
            {
                DeleteRecording(data);
            }
            finally
            {
                if (!leaveOn)
                    DisablePerfMap(runtime, perfMap);
            }
        }
    }

    /// <summary>Deletes a recording, and the copy perf keeps of an earlier one at the same path, as a failed attempt of <see cref="RecordAsync"/> leaves.</summary>
    internal static void DeleteRecording(string data)
    {
        File.Delete(data);
        File.Delete(data + ".old");
    }

    /// <summary>
    /// Runs <paramref name="record"/>, starting it again after <paramref name="pause"/> when perf failed while attaching to the target,
    /// up to <see cref="RecordAttempts"/> times.
    /// </summary>
    /// <param name="record">Runs <c>perf record</c>, throwing <see cref="PerfFailedException"/> when it fails.</param>
    /// <param name="window">How long the recording runs; a failure after that is not retried, since it would cost the whole window again.</param>
    /// <remarks>
    /// <c>perf record -p</c> (6.8 and 7.0 alike) exits with 255 and no error of its own, only its usual warning about kernel symbols, when threads
    /// of the target end while it attaches to them. A process that starts and ends threads quickly, as a thread pool under load does, hits this
    /// often and in bursts, so the attempts are spaced out. Any other failure, such as a missing permission, would only repeat, so it is reported at once.
    /// </remarks>
    /// <exception cref="InvalidOperationException">perf failed for another reason, or failed to attach every time.</exception>
    internal static async Task RecordAsync(Func<Task> record, TimeSpan window, TimeSpan pause, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                await record();
                return;
            }
            catch (PerfFailedException e) when (FailedToAttach(e.ExitCode, e.Error) && Stopwatch.GetElapsedTime(started) < window && !cancellationToken.IsCancellationRequested)
            {
                if (attempt == RecordAttempts)
                    throw new InvalidOperationException($"{e.Message}\nperf failed {RecordAttempts} times. It fails when threads of the target end while it attaches to them, " +
                                                        "which is frequent while the process starts and ends many threads, as under load. Try again, or with a shorter window.", e);
                await Task.Delay(pause, cancellationToken);
            }
        }
    }

    /// <summary>Whether perf failed the way it does when threads of the target end while it attaches: exit code 255 and no error beyond its warning about kernel symbols.</summary>
    internal static bool FailedToAttach(int exitCode, string error)
    {
        if (exitCode != 255)
            return false;

        foreach (string line in error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line is not ("Couldn't record kernel reference relocation symbol"
                or "Symbol resolution may be skewed if relocation was used (e.g. kexec)."
                or "Check /proc/kallsyms permission or run as root."))
                return false;
        }

        return true;
    }

    /// <summary>Whether the runtime writes its perf map without being asked: its environment enables it, or the running process wrote the map.</summary>
    /// <remarks><c>PerfMapEnabled</c> 1 writes the map and jit dumps, 3 the map only, and 2 jit dumps only, which needs the map turned on.</remarks>
    internal static bool PerfMapAlreadyOn(IReadOnlyDictionary<string, string> environment, bool mapWritten) =>
        mapWritten || Setting(environment, "PerfMapEnabled")?.Trim() is "1" or "3";

    /// <summary>Whether the runtime writes jit dumps, which <see cref="DiagnosticsClient.DisablePerfMap"/> would end along with the map.</summary>
    internal static bool WritesJitDumps(IReadOnlyDictionary<string, string> environment, bool jitDumpWritten) =>
        jitDumpWritten || Setting(environment, "PerfMapEnabled")?.Trim() is "1" or "2";

    /// <summary>Whether <paramref name="path"/> was certainly written since <paramref name="started"/>, as opposed to missing or possibly left by an earlier process with the same PID.</summary>
    /// <remarks>
    /// The runtime leaves its perf map behind when it exits, and a container's first process has the same PID after a restart. The start time is
    /// known to within a second, so a file written in the second after it may still be an earlier process's and does not count: a fresh map is
    /// asked for instead. The running process's own map is rewritten as it compiles methods, so it soon falls outside that second.
    /// </remarks>
    internal static bool WrittenSince(string path, DateTime started) =>
        File.Exists(path) && File.GetLastWriteTimeUtc(path) >= started.AddSeconds(1);

    /// <summary>When <paramref name="pid"/> started, in UTC, or <see cref="DateTime.MinValue"/> when that cannot be read, so that any file counts as its own.</summary>
    private static DateTime StartTime(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>A runtime setting from the environment, under the <c>DOTNET_</c> prefix or the older <c>COMPlus_</c> one.</summary>
    private static string? Setting(IReadOnlyDictionary<string, string> environment, string setting) =>
        environment.TryGetValue("DOTNET_" + setting, out string? value) || environment.TryGetValue("COMPlus_" + setting, out value) ? value : null;

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

    /// <summary>Turns off the perf map this sample turned on and deletes its files; does nothing for a map that was on before.</summary>
    private static void DisablePerfMap(DiagnosticsClient? runtime, string perfMap)
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
        File.Delete(perfMap);
        File.Delete(Path.Combine(Path.GetDirectoryName(perfMap)!, Path.GetFileName(perfMap).Replace("perf-", "perfinfo-", StringComparison.Ordinal)));
    }

    /// <summary>Where the runtime writes the perf map, as this process sees it: <c>PerfMapJitDumpPath</c>, or <c>/tmp</c>.</summary>
    /// <remarks>An absolute folder is in the target's file system, reached through its root; a relative one is relative to its working directory.</remarks>
    internal static string PerfMapPath(int pid, IReadOnlyDictionary<string, string> environment)
    {
        string folder = Setting(environment, "PerfMapJitDumpPath") is { Length: > 0 } configured ? configured : "/tmp";
        return folder.StartsWith('/') ? $"/proc/{pid}/root{folder.TrimEnd('/')}/perf-{pid}.map" : $"/proc/{pid}/cwd/{folder.TrimEnd('/')}/perf-{pid}.map";
    }

    private static IReadOnlyDictionary<string, string> ReadEnvironment(int pid)
    {
        Dictionary<string, string> environment = [with(StringComparer.Ordinal)];
        try
        {
            foreach (string variable in File.ReadAllText($"/proc/{pid}/environ").Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = variable.IndexOf('=');
                if (equals > 0)
                    environment[variable[..equals]] = variable[(equals + 1)..];
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // stdout carries the MCP protocol.
            Console.Error.WriteLine($"Cannot read the environment of process {pid}, so its perf map is looked for at the default path: {e.Message}");
        }

        return environment;
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
            throw new PlatformNotSupportedException("perf is not installed. A sidecar image has it unless it was built on a base image without apt; on a Linux host install linux-perf or linux-tools for the running kernel.");
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
            {
                string message = (await error).Trim();
                throw new PerfFailedException($"perf {arguments[0]} failed with exit code {process.ExitCode}: {message}", process.ExitCode, message);
            }
        }
    }
}

/// <summary>perf exited with a failure code.</summary>
/// <param name="ExitCode">perf's exit code.</param>
/// <param name="Error">What perf wrote to its standard error.</param>
internal sealed class PerfFailedException(string message, int exitCode, string error) : InvalidOperationException(message)
{
    public int ExitCode { get; } = exitCode;

    public string Error { get; } = error;
}
