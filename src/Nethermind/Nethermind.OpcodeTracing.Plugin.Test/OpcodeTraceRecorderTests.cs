// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
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
    public void RealTime_attaches_opcode_block_tracer()
    {
        using OpcodeTraceRecorder recorder = CreateRecorder(new OpcodeTracingConfig
        {
            Enabled = true,
            Mode = "RealTime",
            RecentBlocks = 5,
            OutputDirectory = _tempDir
        });

        recorder.Prepare();

        Assert.That(recorder.AttachRealTime(), Is.InstanceOf<OpcodeBlockTracer>());
    }

    [Test]
    public void Prepare_returns_false_on_invalid_config()
    {
        // StartBlock > EndBlock fails validation; the wrapper catches the throw and reports failure so the caller disables tracing.
        using OpcodeTraceRecorder recorder = CreateRecorder(new OpcodeTracingConfig
        {
            Enabled = true,
            Mode = "RealTime",
            StartBlock = 10,
            EndBlock = 1,
            OutputDirectory = _tempDir
        });

        Assert.That(recorder.Prepare(), Is.False);
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

        recorder.Prepare();
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
        recorder.Prepare();
        recorder.AttachRealTime();

        await recorder.DisposeAsync();
        Assert.DoesNotThrowAsync(async () => await recorder.DisposeAsync());
    }

    private static OpcodeTraceRecorder CreateRecorder(IOpcodeTracingConfig config)
    {
        INethermindApi api = Substitute.For<INethermindApi>();
        api.LogManager.Returns(LimboLogs.Instance);
        api.BlockTree.Returns(Substitute.For<IBlockTree>());
        api.SyncModeSelector.Returns(Substitute.For<ISyncModeSelector>());
        return new(config, Substitute.For<IReadOnlyTxProcessingEnvFactory>(), api, LimboLogs.Instance);
    }
}
