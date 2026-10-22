// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Config.Test;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.KeepAlive;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test.KeepAlive;

[Parallelizable(ParallelScope.All)]
public class EngineKeepAliveTests
{
    private const int WaitMs = 5_000;
    private const int PollMs = 5;
    private const int NeverMs = 600_000;

    private static string NewDirectory()
    {
        // The test's work directory, not /tmp: it is on a real file system, where O_DIRECT works.
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "keepalive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static EngineKeepAliveService Create(
        EngineKeepAliveMode mode,
        int intervalMs,
        string? filePath = null,
        bool diskSupported = true,
        bool allowDirect = true,
        int fileSize = 64 * 1024) =>
        new(mode, intervalMs, filePath ?? Path.Combine(NewDirectory(), EngineKeepAliveService.FileName), diskSupported, allowDirect, fileSize, LimboLogs.Instance);

    [Test]
    public void Defaults_are_off_at_fifty_milliseconds()
    {
        IMergeConfig config = new ConfigProvider().GetConfig<IMergeConfig>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(config.EngineKeepAlive, Is.EqualTo(EngineKeepAliveMode.Off));
            Assert.That(config.EngineKeepAliveIntervalMs, Is.EqualTo(50));
        }
    }

    [TestCase(nameof(IMergeConfig.EngineKeepAlive))]
    [TestCase(nameof(IMergeConfig.EngineKeepAliveIntervalMs))]
    public void Documented_default_matches_the_implementation(string propertyName) =>
        StandardConfigTests.CheckDefault(typeof(IMergeConfig).GetProperty(propertyName)!, new MergeConfig());

    [TestCase("Both", EngineKeepAliveMode.Both)]
    [TestCase("Disk", EngineKeepAliveMode.Disk)]
    [TestCase("cpu", EngineKeepAliveMode.Cpu)]
    [TestCase("Off", EngineKeepAliveMode.Off)]
    public void Mode_and_interval_are_read_from_the_command_line(string mode, EngineKeepAliveMode expected)
    {
        ConfigProvider configProvider = new();
        configProvider.AddSource(new ArgsConfigSource(new Dictionary<string, string>
        {
            { "Merge.EngineKeepAlive", mode },
            { "Merge.EngineKeepAliveIntervalMs", "20" },
        }));

        IMergeConfig config = configProvider.GetConfig<IMergeConfig>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(config.EngineKeepAlive, Is.EqualTo(expected));
            Assert.That(config.EngineKeepAliveIntervalMs, Is.EqualTo(20));
        }
    }

    [Test]
    public void Off_never_starts_a_thread()
    {
        using EngineKeepAliveService keepAlive = Create(EngineKeepAliveMode.Off, 1);
        keepAlive.Start();
        keepAlive.Signal();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(keepAlive.IsStarted, Is.False);
            Assert.That(keepAlive.IsRunning, Is.False);
            Assert.That(keepAlive.Ticks, Is.Zero);
        }
    }

    [Test]
    public void Ticks_every_interval_and_measures_the_wake_up_jitter()
    {
        using EngineKeepAliveService keepAlive = Create(EngineKeepAliveMode.Cpu, 5);
        keepAlive.Start();

        Assert.That(() => keepAlive.Ticks, Is.GreaterThanOrEqualTo(10).After(WaitMs, PollMs));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(keepAlive.IsRunning, Is.True);
            Assert.That(keepAlive.SignalledTicks, Is.Zero);
            Assert.That(keepAlive.CpuTicks, Is.GreaterThanOrEqualTo(10));
            Assert.That(keepAlive.MaxJitterMicros, Is.GreaterThanOrEqualTo(keepAlive.LastJitterMicros));
            Assert.That(keepAlive.Reads, Is.Zero, "Cpu mode reads nothing");
        }
    }

    [Test]
    public void Signal_ticks_at_once()
    {
        using EngineKeepAliveService keepAlive = Create(EngineKeepAliveMode.Cpu, NeverMs);
        keepAlive.Start();

        for (int i = 1; i <= 3; i++)
        {
            keepAlive.Signal();
            Assert.That(() => keepAlive.SignalledTicks, Is.EqualTo(i).After(WaitMs, PollMs), $"signal {i}");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(keepAlive.Ticks, Is.EqualTo(3), "the timer never fired");
            Assert.That(keepAlive.CpuTicks, Is.EqualTo(3));
        }
    }

    [Test]
    public void Signal_before_start_does_nothing()
    {
        using EngineKeepAliveService keepAlive = Create(EngineKeepAliveMode.Cpu, 1);
        keepAlive.Signal();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(keepAlive.IsStarted, Is.False);
            Assert.That(keepAlive.IsRunning, Is.False);
            Assert.That(keepAlive.Ticks, Is.Zero);
        }
    }

    [Test]
    public void Start_is_idempotent()
    {
        using EngineKeepAliveService keepAlive = Create(EngineKeepAliveMode.Cpu, NeverMs);
        keepAlive.Start();
        keepAlive.Start();
        keepAlive.Signal();

        Assert.That(() => keepAlive.SignalledTicks, Is.EqualTo(1).After(WaitMs, PollMs));
        Assert.That(keepAlive.Ticks, Is.EqualTo(1));
    }

    [Test]
    public void Disk_mode_is_a_no_op_where_it_is_unsupported()
    {
        string path = Path.Combine(NewDirectory(), EngineKeepAliveService.FileName);
        using EngineKeepAliveService keepAlive = Create(EngineKeepAliveMode.Disk, 2, path, diskSupported: false);
        keepAlive.Start();

        Assert.That(() => keepAlive.Ticks, Is.GreaterThanOrEqualTo(5).After(WaitMs, PollMs));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(keepAlive.IsRunning, Is.True, "the thread keeps ticking");
            Assert.That(keepAlive.Reads, Is.Zero);
            Assert.That(keepAlive.ReadErrors, Is.Zero);
            Assert.That(keepAlive.DiskReadMode, Is.EqualTo(DiskKeepAliveReadMode.None));
            Assert.That(File.Exists(path), Is.False, "no file is created");
        }
    }

    [Test]
    public void Disk_mode_turns_itself_off_when_the_file_cannot_be_created()
    {
        string blocker = Path.Combine(NewDirectory(), "not-a-directory");
        File.WriteAllText(blocker, "");
        string path = Path.Combine(blocker, "sub", EngineKeepAliveService.FileName);

        using EngineKeepAliveService keepAlive = Create(EngineKeepAliveMode.Both, 2, path);
        keepAlive.Start();

        Assert.That(() => keepAlive.Ticks, Is.GreaterThanOrEqualTo(5).After(WaitMs, PollMs));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(keepAlive.IsRunning, Is.True, "the CPU keep-alive keeps going");
            Assert.That(keepAlive.CpuTicks, Is.GreaterThanOrEqualTo(5));
            Assert.That(keepAlive.Reads, Is.Zero);
            Assert.That(keepAlive.DiskReadMode, Is.EqualTo(DiskKeepAliveReadMode.None));
        }
    }

    [Test]
    public void Dispose_stops_the_thread()
    {
        EngineKeepAliveService keepAlive = Create(EngineKeepAliveMode.Cpu, 1);
        keepAlive.Start();
        Assert.That(() => keepAlive.Ticks, Is.GreaterThanOrEqualTo(2).After(WaitMs, PollMs));

        keepAlive.Dispose();

        Assert.That(keepAlive.IsRunning, Is.False);
        long ticks = keepAlive.Ticks;
        Assert.DoesNotThrow(keepAlive.Signal);
        Assert.DoesNotThrow(keepAlive.Start);
        Assert.DoesNotThrow(keepAlive.Dispose);
        Task.Delay(20).Wait();
        Assert.That(keepAlive.Ticks, Is.EqualTo(ticks));
    }

    [Test]
    public void Dispose_before_start_is_safe()
    {
        EngineKeepAliveService keepAlive = Create(EngineKeepAliveMode.Both, 1);
        keepAlive.Dispose();
        keepAlive.Start();

        Assert.That(keepAlive.IsRunning, Is.False);
    }

    [Test]
    [Platform("Linux")]
    public void Probe_reads_rotating_blocks_with_O_DIRECT()
    {
        string path = Path.Combine(NewDirectory(), EngineKeepAliveService.FileName);
        const int fileSize = 16 * DiskReadProbe.BlockSize;

        using (DiskReadProbe probe = DiskReadProbe.Open(path, fileSize, allowDirect: true))
        {
            Assert.That(probe.Mode, Is.EqualTo(DiskKeepAliveReadMode.Direct));
            for (int i = 0; i < 40; i++) Assert.That(probe.Read(), Is.GreaterThanOrEqualTo(0));
        }

        byte[] content = File.ReadAllBytes(path);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(content, Has.Length.EqualTo(fileSize));
            Assert.That(content.Count(static b => b != 0), Is.GreaterThan(fileSize / 2), "written with random bytes, not a hole");
            Assert.That(File.Exists(path + ".tmp"), Is.False);
        }
    }

    [Test]
    [Platform("Linux")]
    public void Probe_reuses_an_existing_file_and_replaces_one_of_another_size()
    {
        string path = Path.Combine(NewDirectory(), EngineKeepAliveService.FileName);
        const int fileSize = 4 * DiskReadProbe.BlockSize;

        DiskReadProbe.Open(path, fileSize, allowDirect: true).Dispose();
        byte[] first = File.ReadAllBytes(path);
        DiskReadProbe.Open(path, fileSize, allowDirect: true).Dispose();
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(first), "a file of the right size is kept");

        File.WriteAllBytes(path, new byte[100]);
        using DiskReadProbe probe = DiskReadProbe.Open(path, fileSize, allowDirect: true);
        Assert.That(new FileInfo(path).Length, Is.EqualTo(fileSize));
        Assert.That(probe.Read(), Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    [Platform("Linux")]
    public void Probe_falls_back_to_dropping_the_page_cache()
    {
        string path = Path.Combine(NewDirectory(), EngineKeepAliveService.FileName);

        using DiskReadProbe probe = DiskReadProbe.Open(path, 8 * DiskReadProbe.BlockSize, allowDirect: false);

        Assert.That(probe.Mode, Is.EqualTo(DiskKeepAliveReadMode.DropCache));
        for (int i = 0; i < 20; i++) Assert.That(probe.Read(), Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    [Platform("Linux")]
    public void Probe_refuses_a_size_that_is_not_whole_blocks()
    {
        string path = Path.Combine(NewDirectory(), EngineKeepAliveService.FileName);

        Assert.Throws<ArgumentOutOfRangeException>(() => DiskReadProbe.Open(path, DiskReadProbe.BlockSize + 1, allowDirect: true));
        Assert.That(File.Exists(path), Is.False);
    }

    [Test]
    [Platform(Exclude = "Linux")]
    public void Probe_refuses_other_platforms() =>
        Assert.Throws<PlatformNotSupportedException>(() => DiskReadProbe.Open(Path.Combine(NewDirectory(), EngineKeepAliveService.FileName), DiskReadProbe.BlockSize, allowDirect: true));

    [TestCase(true, DiskKeepAliveReadMode.Direct)]
    [TestCase(false, DiskKeepAliveReadMode.DropCache)]
    [Platform("Linux")]
    public void Disk_mode_reads_on_every_tick(bool allowDirect, DiskKeepAliveReadMode expectedMode)
    {
        string path = Path.Combine(NewDirectory(), EngineKeepAliveService.FileName);
        using EngineKeepAliveService keepAlive = Create(EngineKeepAliveMode.Disk, NeverMs, path, allowDirect: allowDirect);
        keepAlive.Start();

        for (int i = 1; i <= 5; i++)
        {
            keepAlive.Signal();
            Assert.That(() => keepAlive.Reads, Is.EqualTo(i).After(WaitMs, PollMs), $"read {i}");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(keepAlive.DiskReadMode, Is.EqualTo(expectedMode));
            Assert.That(keepAlive.ReadErrors, Is.Zero);
            Assert.That(keepAlive.CpuTicks, Is.Zero, "Disk mode does not spin");
            Assert.That(keepAlive.MaxReadMicros, Is.GreaterThanOrEqualTo(keepAlive.LastReadMicros));
            Assert.That(new FileInfo(path).Length, Is.EqualTo(64 * 1024));
        }
    }

    [Test]
    [Platform("Linux")]
    public void Both_reads_and_spins_on_the_timer()
    {
        using EngineKeepAliveService keepAlive = Create(EngineKeepAliveMode.Both, 2);
        keepAlive.Start();

        Assert.That(() => keepAlive.Reads, Is.GreaterThanOrEqualTo(5).After(WaitMs, PollMs));
        Assert.That(keepAlive.CpuTicks, Is.GreaterThanOrEqualTo(5));
    }

    [Test]
    [Platform("Linux")]
    public void Dispose_stops_the_disk_keep_alive()
    {
        string path = Path.Combine(NewDirectory(), EngineKeepAliveService.FileName);
        EngineKeepAliveService keepAlive = Create(EngineKeepAliveMode.Disk, 1, path);
        keepAlive.Start();
        Assert.That(() => keepAlive.Reads, Is.GreaterThanOrEqualTo(2).After(WaitMs, PollMs));

        keepAlive.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(keepAlive.IsRunning, Is.False);
            Assert.That(keepAlive.DiskReadMode, Is.EqualTo(DiskKeepAliveReadMode.None));
        }
    }

    [Test]
    public async Task Forkchoice_update_starts_the_keep_alive_after_a_valid_answer_and_then_signals()
    {
        using EngineKeepAliveService keepAlive = Create(EngineKeepAliveMode.Cpu, NeverMs);
        IForkchoiceUpdatedHandler inner = Substitute.For<IForkchoiceUpdatedHandler>();
        ForkchoiceStateV1 state = new(Keccak.Zero, Keccak.Zero, Keccak.Zero);
        inner.Handle(state, null, 1).Returns(ForkchoiceUpdatedV1Result.Syncing);
        KeepAliveForkchoiceUpdatedHandler handler = new(inner, keepAlive);

        await handler.Handle(state, null, 1);
        Assert.That(keepAlive.IsStarted, Is.False, "not while syncing");

        inner.Handle(state, null, 1).Returns(ForkchoiceUpdatedV1Result.Valid(null, Keccak.Zero));
        ResultWrapper<ForkchoiceUpdatedV1Result> result = await handler.Handle(state, null, 1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Data!.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid), "the answer is passed through");
            Assert.That(keepAlive.IsStarted, Is.True);
            Assert.That(keepAlive.SignalledTicks, Is.Zero, "the signal came before the start");
        }

        await handler.Handle(state, null, 1);
        Assert.That(() => keepAlive.SignalledTicks, Is.EqualTo(1).After(WaitMs, PollMs));
        await inner.Received(3).Handle(state, null, 1);
    }

    [Test]
    public async Task Get_blobs_signals_a_tick()
    {
        using EngineKeepAliveService keepAlive = Create(EngineKeepAliveMode.Cpu, NeverMs);
        keepAlive.Start();
        IAsyncHandler<byte[][], IReadOnlyList<BlobAndProofV1?>> inner = Substitute.For<IAsyncHandler<byte[][], IReadOnlyList<BlobAndProofV1?>>>();
        byte[][] request = [new byte[32]];
        inner.HandleAsync(request).Returns(ResultWrapper<IReadOnlyList<BlobAndProofV1?>>.Success([]));
        KeepAliveGetBlobsHandler<byte[][], IReadOnlyList<BlobAndProofV1?>> handler = new(inner, keepAlive);

        ResultWrapper<IReadOnlyList<BlobAndProofV1?>> result = await handler.HandleAsync(request);

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.That(() => keepAlive.SignalledTicks, Is.EqualTo(1).After(WaitMs, PollMs));
        await inner.Received(1).HandleAsync(request);
    }
}
