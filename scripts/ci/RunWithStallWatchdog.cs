#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;

if (args.FirstOrDefault() == "--child")
{
    await File.WriteAllTextAsync(args[1], Environment.ProcessId + "\n");
    var start = new ProcessStartInfo(args[2]) { UseShellExecute = false };
    foreach (var argument in args[3..]) start.ArgumentList.Add(argument);
    try
    {
        using var child = Process.Start(start)!;
        await child.WaitForExitAsync();
        return child.ExitCode;
    }
    catch (System.ComponentModel.Win32Exception exception)
    {
        Console.Error.WriteLine(exception.Message);
        return exception.NativeErrorCode == 2 ? 127 : 126;
    }
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
using var signal = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); });
try
{
    if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("The build watchdog requires Linux process groups.");
    if (args.Length == 0) throw new ArgumentException("Usage: RunWithStallWatchdog.cs <command> [args...]");
    string Env(string key, string fallback) => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)) ? fallback : Environment.GetEnvironmentVariable(key)!;
    TimeSpan Seconds(string key, string fallback) => TimeSpan.FromSeconds(double.Parse(Env(key, fallback), CultureInfo.InvariantCulture));
    var stall = Seconds("STALL_SECONDS", "300");
    var poll = Seconds("POLL_SECONDS", "15");
    var diagnostic = Seconds("DIAGNOSTIC_SECONDS", "120");
    var install = Seconds("INSTALL_TIMEOUT_SECONDS", "30");
    var stack = Seconds("STACK_TIMEOUT_SECONDS", "10");
    var termination = Seconds("TERMINATION_SECONDS", "30");
    if (stall < TimeSpan.Zero || poll <= TimeSpan.Zero || diagnostic <= TimeSpan.Zero || install <= TimeSpan.Zero || stack <= TimeSpan.Zero || termination < TimeSpan.Zero)
        throw new ArgumentOutOfRangeException("interval", "Watchdog intervals must be positive (stall and termination may be zero).");
    var directory = Path.GetFullPath(Env("DIAG_DIR", Path.Combine(Env("RUNNER_TEMP", "/tmp"), "stall-diagnostics")));
    Directory.CreateDirectory(directory);
    var pidFile = Path.Combine(directory, "command.pid");
    File.Delete(pidFile);
    using var log = new FileStream(Path.Combine(directory, "command.log"), FileMode.Create, FileAccess.Write, FileShare.Read);
    using var writeLock = new SemaphoreSlim(1);
    var size = 0L;
    var start = new ProcessStartInfo("setsid") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add("--wait");
    start.ArgumentList.Add(Environment.ProcessPath!);
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
    foreach (var argument in new[] { "--child", pidFile }.Concat(args)) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    using var readersCancellation = new CancellationTokenSource();
    using var console = Console.OpenStandardOutput();
    async Task Forward(Stream source, Stream destination)
    {
        var buffer = new byte[8192];
        while (await source.ReadAsync(buffer, readersCancellation.Token) is var count && count > 0)
        {
            await writeLock.WaitAsync(readersCancellation.Token);
            try
            {
                await log.WriteAsync(buffer.AsMemory(0, count), readersCancellation.Token);
                await log.FlushAsync(readersCancellation.Token);
                Interlocked.Add(ref size, count);
                await destination.WriteAsync(buffer.AsMemory(0, count), readersCancellation.Token);
                await destination.FlushAsync(readersCancellation.Token);
            }
            finally { writeLock.Release(); }
        }
    }
    var readers = Task.WhenAll(Forward(process.StandardOutput.BaseStream, console), Forward(process.StandardError.BaseStream, console));
    var exited = process.WaitForExitAsync(CancellationToken.None);
    var stalled = false;
    var finished = false;
    try
    {
        var lastSize = -1L;
        var silent = TimeSpan.Zero;
        while (!process.HasExited)
        {
            await Task.WhenAny(exited, Task.Delay(poll, cancellation.Token));
            cancellation.Token.ThrowIfCancellationRequested();
            if (process.HasExited) break;
            var current = Interlocked.Read(ref size);
            silent = current == lastSize ? silent + poll : TimeSpan.Zero;
            lastSize = current;
            if (silent < stall) continue;
            stalled = true;
            Console.WriteLine($"::error::No output for {stall.TotalSeconds}s — collecting diagnostics into {directory}");
            await Diagnostics(directory, diagnostic, install, stack, cancellation.Token);
            break;
        }
        if (stalled) await Terminate(process, pidFile, termination);
        await exited;
        if (await Task.WhenAny(readers, Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token)) != readers)
            readersCancellation.Cancel();
        try { await readers; }
        catch (OperationCanceledException) when (!cancellation.Token.IsCancellationRequested) { Console.Error.WriteLine("Stopped following inherited output pipes after the command exited."); }
        cancellation.Token.ThrowIfCancellationRequested();
        finished = true;
        return stalled ? 1 : process.ExitCode;
    }
    finally
    {
        if (!finished)
        {
            await Terminate(process, pidFile, cancellation.Token.IsCancellationRequested ? TimeSpan.FromSeconds(1) : termination);
            readersCancellation.Cancel();
            try { await readers; }
            catch (OperationCanceledException) { Console.Error.WriteLine("Watchdog output forwarding cancelled."); }
        }
    }
}
catch (OperationCanceledException) { return 143; }
catch (Exception exception) { Console.Error.WriteLine(exception.Message); return 1; }

static async Task Terminate(Process process, string pidFile, TimeSpan grace)
{
    var group = process.Id;
    if (File.Exists(pidFile) && int.TryParse((await File.ReadAllTextAsync(pidFile)).Trim(), out var parsed) && parsed > 0) group = parsed;
    var term = await Signal("-TERM", $"-{group}");
    if (term != 0 && !process.HasExited) await Signal("-TERM", process.Id.ToString(CultureInfo.InvariantCulture));
    var deadline = Stopwatch.StartNew();
    while (deadline.Elapsed < grace)
    {
        if (await Signal("-0", $"-{group}") != 0) break;
        await Task.Delay(TimeSpan.FromMilliseconds(100));
    }
    await Signal("-KILL", $"-{group}");
    if (!process.HasExited) process.Kill(entireProcessTree: true);
    await process.WaitForExitAsync(CancellationToken.None);
}

static async Task<int> Signal(string signal, string target)
{
    var result = await Capture("kill", [signal, "--", target], TimeSpan.FromSeconds(2), CancellationToken.None);
    return result.Code;
}

static async Task Diagnostics(string directory, TimeSpan total, TimeSpan install, TimeSpan stack, CancellationToken cancellationToken)
{
    var elapsed = Stopwatch.StartNew();
    TimeSpan Remaining() => total - elapsed.Elapsed;
    TimeSpan Clamp(TimeSpan timeout) => timeout < Remaining() ? timeout : Remaining();
    using (var system = new StreamWriter(Path.Combine(directory, "system.txt"), append: false, new UTF8Encoding(false)))
    {
        await system.WriteLineAsync($"stalled at {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}");
        foreach (var (title, command, arguments) in new[] { ("uptime / load", "uptime", Array.Empty<string>()), ("memory", "free", new[] { "-m" }), ("disk", "df", new[] { "-h" }), ("process tree", "ps", new[] { "-ef", "--forest" }), ("threads", "ps", new[] { "-eLf" }) })
        {
            await system.WriteLineAsync($"=== {title} ===");
            var result = await Capture(command, arguments, Remaining(), cancellationToken);
            await system.WriteAsync(result.Output);
        }
    }
    var installation = await Capture("dotnet", ["tool", "install", "--global", "dotnet-stack"], Clamp(install), cancellationToken);
    await File.WriteAllTextAsync(Path.Combine(directory, "dotnet-stack-install.log"), installation.Output, cancellationToken);
    var path = (Environment.GetEnvironmentVariable("PATH") ?? "") + ":" + Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "", ".dotnet/tools");
    var executable = path.Split(':').Select(part => Path.Combine(part, "dotnet-stack")).FirstOrDefault(File.Exists);
    if (executable is not null && Remaining() > TimeSpan.Zero)
    {
        var pids = await Capture("pgrep", ["-x", "dotnet|VBCSCompiler"], Remaining(), cancellationToken);
        foreach (var pid in pids.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (Remaining() <= TimeSpan.Zero) break;
            if (!int.TryParse(pid, out var number) || number <= 0) continue;
            var result = await Capture(executable, ["report", "--process-id", pid], Clamp(stack), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory, $"stack-{pid}.txt"), result.Output, cancellationToken);
        }
    }
    var debug = Environment.GetEnvironmentVariable("MSBUILDDEBUGPATH");
    if (!string.IsNullOrEmpty(debug) && Directory.Exists(debug))
    {
        var destination = Directory.CreateDirectory(Path.Combine(directory, "msbuild-debug"));
        foreach (var file in Directory.GetFiles(debug))
        {
            try
            {
                using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var end = source.Length;
                var length = Math.Min(end, 5_000_000);
                source.Seek(end - length, SeekOrigin.Begin);
                using var output = File.Create(Path.Combine(destination.FullName, Path.GetFileName(file)));
                var buffer = new byte[81920];
                while (length > 0)
                {
                    var count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), cancellationToken);
                    if (count == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    length -= count;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { Console.Error.WriteLine($"Could not copy scheduler diagnostic {file}: {exception.Message}"); }
        }
    }
}

static async Task<(int Code, string Output)> Capture(string command, string[] arguments, TimeSpan timeout, CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    if (timeout <= TimeSpan.Zero) return (124, "");
    var start = new ProcessStartInfo(command) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = new Process { StartInfo = start };
    try { process.Start(); }
    catch (System.ComponentModel.Win32Exception exception) { return (127, exception.Message + "\n"); }
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    deadline.CancelAfter(timeout);
    var stdoutText = new StringBuilder();
    var stderrText = new StringBuilder();
    async Task Read(StreamReader reader, StringBuilder text)
    {
        var buffer = new char[8192];
        while (await reader.ReadAsync(buffer, deadline.Token) is var count && count > 0) text.Append(buffer, 0, count);
    }
    var stdout = Read(process.StandardOutput, stdoutText);
    var stderr = Read(process.StandardError, stderrText);
    try
    {
        await process.WaitForExitAsync(deadline.Token);
        await Task.WhenAll(stdout, stderr);
        return (process.ExitCode, stdoutText.ToString() + stderrText);
    }
    catch (OperationCanceledException)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(CancellationToken.None);
        try { await Task.WhenAll(stdout, stderr); }
        catch (OperationCanceledException) { Console.Error.WriteLine($"Diagnostic command {command} timed out or was cancelled."); }
        cancellationToken.ThrowIfCancellationRequested();
        return (124, stdoutText.ToString() + stderrText + "Diagnostic command timed out.\n");
    }
}
