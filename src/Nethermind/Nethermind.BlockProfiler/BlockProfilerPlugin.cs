// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Threading;
using Autofac;
using Autofac.Core;
using JetBrains.Profiler.Api;
using Nethermind.Api.Extensions;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;

namespace Nethermind.BlockProfiler;

/// <summary>
/// Diagnostic/benchmark plugin. When <c>NETHERMIND_PROFILE_BLOCKS</c> lists block numbers, it brackets each
/// such block's main-pipeline processing with a JetBrains dotTrace collection window, yielding one snapshot
/// per target block. Inert when the variable is unset; the profiler API calls are no-ops off the profiler.
/// </summary>
public class BlockProfilerPlugin : INethermindPlugin
{
    private readonly FrozenSet<ulong> _targets = ParseTargets();

    public string Name => "BlockProfiler";
    public string Description => "Captures a dotTrace snapshot per configured block number (NETHERMIND_PROFILE_BLOCKS)";
    public string Author => "Nethermind";
    public bool Enabled => _targets.Count > 0;
    public IModule? Module => new BlockProfilerModule(_targets);

    internal static FrozenSet<ulong> ParseTargets()
    {
        string? raw = Environment.GetEnvironmentVariable("NETHERMIND_PROFILE_BLOCKS");
        if (string.IsNullOrWhiteSpace(raw)) return FrozenSet<ulong>.Empty;
        HashSet<ulong> set = [];
        foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ulong.TryParse(part, out ulong n)) set.Add(n);
        }
        return set.ToFrozenSet();
    }
}

public class BlockProfilerModule(FrozenSet<ulong> targets) : Module
{
    protected override void Load(ContainerBuilder builder) =>
        // Decorating IBranchProcessor guarantees the observer is instantiated with the main processor
        // (no eager-activation dance).
        builder
            .AddSingleton(targets)
            .AddDecorator<IBranchProcessor, ProfilingBranchProcessor>();
}

/// <summary>
/// Pass-through <see cref="IBranchProcessor"/> decorator that opens a dotTrace collection window for the
/// duration of a target block's processing (using the per-block <see cref="IBranchProcessor.BlockProcessing"/>
/// / <see cref="IBranchProcessor.BlockProcessed"/> events, which only fire for non-read-only processing).
/// </summary>
public sealed class ProfilingBranchProcessor : IBranchProcessor, IDisposable
{
    // The dotTrace measure session is process-global while branch processors are per-scope, so the
    // collection window is coordinated statically: overlapping processors (e.g. main processing and
    // block production) must not clobber each other's start/stop/save sequence.
    private static readonly object s_profilerSync = new();
    private static bool s_collecting;
    private static int s_armedLogged;
    private static int s_apiUnavailableLogged;

    private readonly IBranchProcessor _inner;
    private readonly FrozenSet<ulong> _targets;
    private readonly ILogger _logger;

    public ProfilingBranchProcessor(IBranchProcessor inner, FrozenSet<ulong> targets, ILogManager logManager)
    {
        _inner = inner;
        _targets = targets;
        _logger = logManager.GetClassLogger<ProfilingBranchProcessor>();
        _inner.BlockProcessing += OnBlockProcessing;
        _inner.BlockProcessed += OnBlockProcessed;
        // Branch processors are scoped; log the arming once per process, not per scope.
        if (Interlocked.Exchange(ref s_armedLogged, 1) == 0)
        {
            if (_logger.IsInfo) _logger.Info($"BlockProfiler armed for blocks: {string.Join(",", _targets)}");
        }
    }

    public Block[] Process(BlockHeader? baseBlock, IReadOnlyList<Block> suggestedBlocks, ProcessingOptions processingOptions, IBlockTracer blockTracer, CancellationToken token = default)
        => _inner.Process(baseBlock, suggestedBlocks, processingOptions, blockTracer, token);

    /// <summary>Whether the profiler session accepts measure-API control (requires dotTrace <c>--use-api</c>).</summary>
    private bool IsMeasureApiAvailable()
    {
        if ((MeasureProfiler.GetFeatures() & MeasureFeatures.Ready) != 0) return true;
        if (Interlocked.Exchange(ref s_apiUnavailableLogged, 1) == 0)
        {
            if (_logger.IsWarn) _logger.Warn(
                "dotTrace measure API is not available: the profiler session was not started with --use-api. " +
                "Per-block snapshots will NOT be captured; only the profiler's own whole-run snapshot is produced.");
        }
        return false;
    }

    private void OnBlockProcessing(object? sender, BlockEventArgs e)
    {
        if (!_targets.Contains(e.Block.Number)) return;
        if (!IsMeasureApiAvailable()) return;
        lock (s_profilerSync)
        {
            // Discard any stray window's samples (e.g. a prior target that errored): StopCollectingData
            // alone keeps the buffer, which would bleed into the next snapshot's SaveData.
            if (s_collecting) MeasureProfiler.DropData();
            MeasureProfiler.StartCollectingData();
            s_collecting = true;
        }
        if (_logger.IsInfo) _logger.Info($"dotTrace: started collecting for block {e.Block.Number}");
    }

    private void OnBlockProcessed(object? sender, BlockProcessedEventArgs e)
    {
        if (!_targets.Contains(e.Block.Number)) return;
        lock (s_profilerSync)
        {
            if (!s_collecting) return;
            s_collecting = false;
            MeasureProfiler.StopCollectingData();
            MeasureProfiler.SaveData(); // one snapshot per target block
        }
        if (_logger.IsInfo) _logger.Info($"dotTrace: saved snapshot for block {e.Block.Number}");
    }

    public event EventHandler<BlockProcessedEventArgs>? BlockProcessed
    {
        add => _inner.BlockProcessed += value;
        remove => _inner.BlockProcessed -= value;
    }

    public event EventHandler<BlocksProcessingEventArgs>? BlocksProcessing
    {
        add => _inner.BlocksProcessing += value;
        remove => _inner.BlocksProcessing -= value;
    }

    public event EventHandler<BlockEventArgs>? BlockProcessing
    {
        add => _inner.BlockProcessing += value;
        remove => _inner.BlockProcessing -= value;
    }

    public event EventHandler<BranchProcessingCompletedEventArgs>? BranchProcessingCompleted
    {
        add => _inner.BranchProcessingCompleted += value;
        remove => _inner.BranchProcessingCompleted -= value;
    }

    public void Dispose()
    {
        _inner.BlockProcessing -= OnBlockProcessing;
        _inner.BlockProcessed -= OnBlockProcessed;
        // The container owns the decorated instance; disposing it here would double-dispose.
    }
}
