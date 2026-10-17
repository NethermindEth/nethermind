// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State.OverridableEnv;

namespace Nethermind.Init;

/// <summary>
/// Writes the diagnostics <see cref="IInitConfig.AutoDump"/> asks for of a block the processing queue rejected.
/// </summary>
/// <remarks>
/// The work runs off the processing pipeline, on a thread of its own (below normal priority where the OS honours it), so
/// neither the verdict on the rejected block nor the next queued block waits for it. The block is re-executed on a world state of its own,
/// opened at its parent, once for all the requested traces. At most one dump runs at a time: a block rejected meanwhile
/// is dropped, so invalid blocks sent back to back cannot queue up work, and each block is dumped once.
/// Disposal lets a running dump finish, so the files of the block a node exits on are still written.
/// </remarks>
public sealed class InvalidBlockDiagnostics(
    IInitConfig initConfig,
    IBlockTree blockTree,
    ISpecProvider specProvider,
    IOverridableEnvFactory envFactory,
    ILifetimeScope rootLifetimeScope,
    IBlockValidationModule[] validationModules,
    ILogManager logManager) : IDisposable
{
    private const int DumpedBlocksCacheSize = 64;
    private const ProcessingOptions ReplayOptions = ProcessingOptions.ForceProcessing | ProcessingOptions.ReadOnlyChain;
    private static readonly TimeSpan CompletionWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CancellationWait = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger = logManager.GetClassLogger<InvalidBlockDiagnostics>();
    private readonly LruKeyCache<Hash256AsKey> _dumpedBlocks = new(DumpedBlocksCacheSize, nameof(InvalidBlockDiagnostics));
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Lock _lock = new();
    private Task _running = Task.CompletedTask;
    private (IOverridableEnv Env, ILifetimeScope Scope)? _replayEnv;
    private bool _disposed;

    public void OnInvalidBlock(object? sender, IBlockProcessingQueue.InvalidBlockEventArgs e)
    {
        DumpOptions dumpOptions = initConfig.AutoDump;
        Block block = e.InvalidBlock;
        if (dumpOptions == DumpOptions.None || block.Hash is null) return;

        lock (_lock)
        {
            if (_disposed || _dumpedBlocks.Get(block.Hash)) return;
            if (!_running.IsCompleted)
            {
                if (_logger.IsInfo) _logger.Info($"Skipped diagnostics of invalid block {block.ToString(Block.Format.FullHashAndNumber)}: an earlier dump is still running.");
                return;
            }

            _dumpedBlocks.Set(block.Hash);
            _running = Task.Factory.StartNew(() => Dump(block, dumpOptions), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }

    private void Dump(Block block, DumpOptions dumpOptions)
    {
        // Assume the default Linux policy (SCHED_OTHER), where priority changes only add native calls.
        if (!OperatingSystem.IsLinux()) Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        try
        {
            BlockTraceDumper.LogDiagnosticRlp(block, _logger,
                (dumpOptions & DumpOptions.Rlp) != 0,
                (dumpOptions & DumpOptions.RlpLog) != 0);

            List<IBlockTracer> tracers = CreateTracers(dumpOptions);
            if (tracers.Count > 0) Replay(block, tracers);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            if (_logger.IsDebug) _logger.Debug($"Diagnostics of invalid block {block.ToString(Block.Format.FullHashAndNumber)} stopped by shutdown.");
        }
        catch (Exception exception)
        {
            if (_logger.IsError) _logger.Error($"Diagnostics of invalid block {block.ToString(Block.Format.FullHashAndNumber)} failed", exception);
        }
    }

    private List<IBlockTracer> CreateTracers(DumpOptions dumpOptions)
    {
        List<IBlockTracer> tracers = [];
        if ((dumpOptions & DumpOptions.Receipts) != 0) tracers.Add(new BlockReceiptsTracer());
        if ((dumpOptions & DumpOptions.Parity) != 0) tracers.Add(new ParityLikeBlockTracer(ParityTraceTypes.StateDiff | ParityTraceTypes.Trace, specProvider));
        if ((dumpOptions & DumpOptions.Geth) != 0) tracers.Add(new GethLikeBlockMemoryTracer(new GethTraceOptions { EnableMemory = true }, specProvider));
        return tracers;
    }

    private void Replay(Block block, List<IBlockTracer> tracers)
    {
        BlockHeader? parent = blockTree.FindParentHeader(block.Header, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        (IOverridableEnv env, ILifetimeScope scope) = _replayEnv ??= CreateReplayEnv();
        if (parent is null || !env.TryBuildAndOverride(parent, null, null, null, out IDisposable? state))
        {
            if (_logger.IsInfo) _logger.Info($"Skipped traces of invalid block {block.ToString(Block.Format.FullHashAndNumber)}: the state of its parent is unavailable.");
            return;
        }

        CompositeBlockTracer tracer = new();
        tracer.AddRange(tracers);
        // The rejected block may still be read by the processing pipeline, so the replay mutates a header of its own.
        Block isolated = block.WithReplacedHeader(block.Header.Clone());
        Either<Hash256, IList<Block>> outcome;
        using (state)
        {
            try
            {
                scope.Resolve<BlockchainProcessorFacade>().Process(isolated, ReplayOptions, tracer, _cancellation.Token);
                outcome = new List<Block> { block };
            }
            catch (InvalidBlockException)
            {
                outcome = block.Hash!;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                BlockTraceDumper.LogTraceFailure(tracer, parent, exception, _logger);
                return;
            }
            finally
            {
                isolated.DisposeAccountChanges();
            }
        }

        foreach (IBlockTracer blockTracer in tracers)
        {
            BlockTraceDumper.LogDiagnosticTrace(blockTracer, outcome, _logger);
        }
    }

    private (IOverridableEnv, ILifetimeScope) CreateReplayEnv()
    {
        IOverridableEnv env = envFactory.Create();
        ILifetimeScope scope = rootLifetimeScope.BeginLifetimeScope(builder => builder
            .AddModule(validationModules)
            .AddModule(env));
        return (env, scope);
    }

    public void Dispose()
    {
        Task running;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            running = _running;
        }

        if (!running.Wait(CompletionWait)) _cancellation.Cancel();
        if (!running.Wait(CancellationWait))
        {
            // Still replaying on the env, so it is left to the process exit rather than disposed under the replay.
            if (_logger.IsWarn) _logger.Warn("Diagnostics of an invalid block did not stop in time.");
            return;
        }

        if (_replayEnv is { } replayEnv)
        {
            replayEnv.Scope.Dispose();
            (replayEnv.Env as IDisposable)?.Dispose();
        }

        _cancellation.Dispose();
    }
}
