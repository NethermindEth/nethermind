// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Diagnostics.NETCore.Client;
using Nethermind.Tools.ProfilingMcp.Analysis;
using Nethermind.Tools.ProfilingMcp.EventPipe;
using Nethermind.Tools.ProfilingMcp.Memory;
using NUnit.Framework;

namespace Nethermind.Tools.ProfilingMcp.Test;

/// <summary>Samples this test process over EventPipe, so a TraceEvent or runtime update that breaks the samplers fails here.</summary>
[NonParallelizable]
public class LiveEventPipeTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(3);

    private string _trace = null!;

    [SetUp]
    public void SetUp() => _trace = Path.Combine(Path.GetTempPath(), WorkspacePaths.NewFileName("live", ".nettrace"));

    [TearDown]
    public void TearDown() => File.Delete(_trace);

    [Test]
    [CancelAfter(120_000)]
    public async Task Cpu_sample_finds_a_busy_method(CancellationToken cancellationToken)
    {
        using CancellationTokenSource stop = new();
        Task busy = Task.Run(() => Spin(stop.Token), CancellationToken.None);
        try
        {
            await CpuSampler.CollectAsync(Environment.ProcessId, Window, _trace, cancellationToken);
        }
        finally
        {
            await stop.CancelAsync();
            await busy;
        }

        FunctionStat? spin = new NetTraceReader().Read(_trace).Functions.FirstOrDefault(static f => f.Name.EndsWith($"{nameof(LiveEventPipeTests)}.{nameof(Spin)}", StringComparison.Ordinal));

        Assert.That(spin?.OwnTime, Is.GreaterThan(Window.TotalMilliseconds / 4), "the spinning thread is sampled for a good part of the window");
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task Cpu_sample_ends_when_its_session_cannot_be_stopped(CancellationToken cancellationToken)
    {
        // Moving the diagnostics socket keeps the running session's connection but fails the new one that stopping it needs.
        string[] sockets = OperatingSystem.IsLinux() ? Directory.GetFiles(Path.GetTempPath(), $"dotnet-diagnostic-{Environment.ProcessId}-*-socket") : [];
        if (sockets.Length != 1)
            Assert.Ignore("Needs the runtime's diagnostics socket, which only Linux exposes as a file.");

        string socket = sockets[0];
        string moved = socket + ".moved";
        try
        {
            Task sample = CpuSampler.CollectAsync(Environment.ProcessId, TimeSpan.FromSeconds(2), _trace, cancellationToken);
            while (!File.Exists(_trace) || new FileInfo(_trace).Length == 0)
                await Task.Delay(50, cancellationToken);
            File.Move(socket, moved);

            Assert.That(async () => await sample.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken), Throws.InstanceOf<ServerNotAvailableException>());
        }
        finally
        {
            if (File.Exists(moved))
                File.Move(moved, socket);
        }
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task Cpu_sample_gives_up_on_a_target_that_stops_responding(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("Stopping a process with SIGSTOP needs Linux.");

        using Process target = await StartIdleProcessAsync(cancellationToken);
        TimeSpan stopTimeout = CpuSampler.StopTimeout;
        try
        {
            CpuSampler.StopTimeout = TimeSpan.FromSeconds(3);
            Task sample = CpuSampler.CollectAsync(target.Id, TimeSpan.FromSeconds(2), _trace, cancellationToken);
            while (!File.Exists(_trace) || new FileInfo(_trace).Length == 0)
                await Task.Delay(50, cancellationToken);
            await Signal("STOP", target.Id);

            Assert.That(async () => await sample.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken), Throws.InstanceOf<TimeoutException>().With.Message.Contains("did not finish"));
        }
        finally
        {
            CpuSampler.StopTimeout = stopTimeout;
            await Signal("CONT", target.Id);
            target.Kill();
        }

        static async Task Signal(string signal, int pid)
        {
            using Process kill = Process.Start("kill", [$"-{signal}", pid.ToString(CultureInfo.InvariantCulture)])!;
            await kill.WaitForExitAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task Write_dump_does_not_start_when_already_cancelled()
    {
        string workspace = Directory.CreateTempSubdirectory("dump-").FullName;
        try
        {
            using CancellationTokenSource cancelled = new();
            await cancelled.CancelAsync();

            // No process has this ID, so reaching the runtime would fail with another exception.
            Assert.That(async () => await new MemoryTools(WorkspacePaths.At(workspace)).WriteDumpAsync(int.MaxValue, cancelled.Token), Throws.InstanceOf<OperationCanceledException>());
            Assert.That(Directory.GetFiles(workspace, "*", SearchOption.AllDirectories), Is.Empty);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Test]
    public void Moving_a_dump_leaves_nothing_behind_when_it_fails()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("Needs /proc.");

        string folder = Directory.CreateTempSubdirectory("move-").FullName;
        try
        {
            // /proc is another file system, so the move copies the file, then fails to delete the source.
            Assert.That(() => MemoryTools.MoveInto("/proc/self/status", Path.Combine(folder, "pid1.dmp")), Throws.InstanceOf<UnauthorizedAccessException>());
            Assert.That(Directory.GetFiles(folder), Is.Empty);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task Write_dump_reaches_the_workspace_from_a_process_of_another_user(CancellationToken cancellationToken)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!OperatingSystem.IsLinux() || Environment.UserName != "root" || path is null || !Array.Exists(path.Split(':'), static folder => File.Exists(Path.Combine(folder, "setpriv"))))
            Assert.Ignore("Needs Linux, root and setpriv to start a process as another user.");

        // Only root can write to it, as to a root-run server's default workspace.
        string workspace = Directory.CreateTempSubdirectory("dump-").FullName;
        using Process target = await StartIdleProcessAsync(cancellationToken, userId: 65534);
        try
        {
            string result = await new MemoryTools(WorkspacePaths.At(workspace)).WriteDumpAsync(target.Id, cancellationToken);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Directory.GetFiles(Path.Combine(workspace, "heap"), "*.dmp"), Has.Length.EqualTo(1), result);
                Assert.That(Directory.GetFiles(Path.GetTempPath(), $"*pid{target.Id}*.dmp"), Is.Empty, "the staged dump is moved");
            }
        }
        finally
        {
            target.Kill();
            Directory.Delete(workspace, recursive: true);
        }
    }

    [TestCase("Name:\tdotnet\nUid:\t1000\t1001\t1000\t1000\nGid:\t1000\t1000\t1000\t1000\n", ExpectedResult = "1001")]
    [TestCase("Name:\tdotnet\n", ExpectedResult = null)]
    public string? Reads_the_effective_user_of_a_process(string status) => MemoryTools.EffectiveUserId(status);

    [Test]
    [CancelAfter(120_000)]
    public async Task Alloc_sample_finds_the_allocated_type(CancellationToken cancellationToken)
    {
        using CancellationTokenSource stop = new();
        Task allocating = Task.Run(() => Allocate(stop.Token), CancellationToken.None);
        try
        {
            await AllocationSampler.CollectAsync(Environment.ProcessId, Window, _trace, cancellationToken);
        }
        finally
        {
            await stop.CancelAsync();
            await allocating;
        }

        AllocationProfile profile = AllocationSampler.Read(_trace, include: null);

        Assert.That(profile.ByType.Select(static t => t.Name), Has.Some.EndsWith(nameof(Allocated)));
    }

    /// <summary>Starts the server's sidecar mode with no idle limit, a .NET process that does nothing at all, and waits for its diagnostics port.</summary>
    /// <param name="userId">The user to run it as, with setpriv, which replaces itself with the process; null for this process's user.</param>
    private static async Task<Process> StartIdleProcessAsync(CancellationToken cancellationToken, int? userId = null)
    {
        string server = Path.Combine(AppContext.BaseDirectory, "Nethermind.Tools.ProfilingMcp");
        ProcessStartInfo info = userId is { } id
            ? new("setpriv", [$"--reuid={id}", $"--regid={id}", "--clear-groups", server, ContainerSidecar.IdleOption])
            : new(server, ContainerSidecar.IdleOption);
        info.UseShellExecute = false;
        info.Environment[ContainerSidecar.IdleMinutesVariable] = "0";
        Process process = Process.Start(info)!;
        try
        {
            while (!DiagnosticsClient.GetPublishedProcesses().Contains(process.Id))
                await Task.Delay(100, cancellationToken);
            return process;
        }
        catch
        {
            process.Kill();
            process.Dispose();
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Spin(CancellationToken stop)
    {
        long sum = 0;
        while (!stop.IsCancellationRequested)
        {
            for (int i = 0; i < 100_000; i++)
                sum += i ^ sum;
        }

        return sum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Allocate(CancellationToken stop)
    {
        Allocated? last = null;
        while (!stop.IsCancellationRequested)
            last = new Allocated(last is null ? 0 : last.A + 1, 0, 0, 0);
        GC.KeepAlive(last);
    }

    private sealed record Allocated(long A, long B, long C, long D);
}
