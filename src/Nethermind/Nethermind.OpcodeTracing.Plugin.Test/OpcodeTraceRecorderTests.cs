// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.OpcodeTracing.Plugin.Tracing;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.OpcodeTracing.Plugin.Test;

public class OpcodeTraceRecorderTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp() => _tempDir = Path.Combine(Path.GetTempPath(), "opcodetrace-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tempDir, true); } catch { /* best effort cleanup */ }
    }

    [Test]
    public async Task RealTime_attaches_opcode_block_tracer()
    {
        using OpcodeTraceRecorder recorder = CreateRecorder(new OpcodeTracingConfig
        {
            Enabled = true,
            Mode = "RealTime",
            RecentBlocks = 5,
            OutputDirectory = _tempDir
        });

        await recorder.PrepareAsync();

        Assert.That(recorder.AttachRealTime(), Is.InstanceOf<OpcodeBlockTracer>());
    }

    [Test]
    public void PrepareAsync_throws_on_invalid_config()
    {
        // StartBlock > EndBlock fails validation; PrepareAsync throws so a misconfigured node aborts startup instead of running with tracing silently off.
        using OpcodeTraceRecorder recorder = CreateRecorder(new OpcodeTracingConfig
        {
            Enabled = true,
            Mode = "RealTime",
            StartBlock = 10,
            EndBlock = 1,
            OutputDirectory = _tempDir
        });

        Assert.ThrowsAsync<InvalidOperationException>(async () => await recorder.PrepareAsync());
    }

    [Test]
    public async Task Retrospective_runs_and_writes_output()
    {
        using OpcodeTraceRecorder recorder = CreateRecorder(new OpcodeTracingConfig
        {
            Enabled = true,
            Mode = "Retrospective",
            StartBlock = 1,
            EndBlock = 2,
            OutputDirectory = _tempDir
        });

        await recorder.PrepareAsync();
        // The returned task is the background trace; awaiting it ensures the trace output has been written.
        await recorder.ExecuteTracingAsync();

        Assert.That(Directory.GetFiles(_tempDir, "*", SearchOption.AllDirectories), Is.Not.Empty);
    }

    [Test]
    public async Task Dispose_is_idempotent()
    {
        OpcodeTraceRecorder recorder = CreateRecorder(new OpcodeTracingConfig
        {
            Enabled = true,
            Mode = "RealTime",
            RecentBlocks = 5,
            OutputDirectory = _tempDir
        });
        await recorder.PrepareAsync();
        recorder.AttachRealTime();

        await recorder.DisposeAsync();
        Assert.DoesNotThrowAsync(async () => await recorder.DisposeAsync());
    }

    private static OpcodeTraceRecorder CreateRecorder(IOpcodeTracingConfig config) =>
        new(config,
            Substitute.For<IReadOnlyTxProcessingEnvFactory>(),
            Substitute.For<IBlockTree>(),
            Substitute.For<ISpecProvider>(),
            Substitute.For<IEthereumEcdsa>(),
            Substitute.For<ISyncModeSelector>(),
            LimboLogs.Instance);
}
