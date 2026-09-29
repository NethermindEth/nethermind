// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.ServiceStopper;
using Nethermind.Logging;

using Nethermind.BeaconChain.Spec;
namespace Nethermind.BeaconChain;

/// <summary>
/// Lifecycle root of the embedded beacon chain consensus driver.
/// </summary>
/// <remarks>
/// Owns the cancellation scope of all driver components (checkpoint sync, beacon sync,
/// P2P, slot timer). Stopped permanently when an external consensus client is detected
/// on the engine API.
/// </remarks>
public sealed class BeaconChainService(
    IBeaconChainConfig config,
    BeaconChainSpec spec,
    BeaconChainStore store,
    PubkeyCache pubkeyCache,
    CheckpointSync checkpointSync,
    BeaconSyncOrchestrator orchestrator,
    ExternalClDetector externalClDetector,
    ILogManager logManager) : IDisposable, IStoppableService
{
    private static readonly TimeSpan DefaultStartRetryDelay = TimeSpan.FromSeconds(30);

    private readonly ILogger _logger = logManager.GetClassLogger<BeaconChainService>();
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Lock _lifecycleLock = new();
    private bool _disposed;
    private Task? _runTask;

    /// <summary>Wait before checkpoint sync starts over after a network failure that outlasted its own retries.</summary>
    internal TimeSpan StartRetryDelay { get; init; } = DefaultStartRetryDelay;

    /// <summary>Checks the database schema version and any persisted anchor, then runs the driver in the background.</summary>
    /// <remarks>Does nothing, not even the schema check, once an external consensus client has been detected.</remarks>
    /// <exception cref="InvalidOperationException">The database cannot be brought to the current schema version (see <see cref="BeaconChainStore.EnsureSchemaVersion"/>), or its anchor state is missing; thrown before the run starts, so node startup fails.</exception>
    /// <exception cref="InvalidDataException">The persisted anchor state holds an invalid sync committee key or a malformed body, or its block is of another fork.</exception>
    /// <exception cref="BeaconStateException">The persisted anchor state or block record is too short or not of its slot's shape.</exception>
    /// <exception cref="NotSupportedException">The persisted anchor state is at a slot before Electra.</exception>
    public Task Start()
    {
        externalClDetector.ExternalClDetected += Stop;
        // Detection may have fired before we subscribed.
        if (config.DisableOnExternalCl && externalClDetector.IsExternalClDetected)
        {
            Stop();
            return _runTask = Task.CompletedTask;
        }

        // A database or resumed anchor this build refuses leaves the execution layer without a driver, so it fails startup instead of the background run.
        store.EnsureSchemaVersion();
        _runTask = RunAsync(LoadPersistedAnchor());
        return _runTask;
    }

    private async Task RunAsync((ForkedBeaconState State, ForkedSignedBeaconBlock? Block, Hash256 BlockRoot)? persistedAnchor)
    {
        CancellationToken token = _cancellationTokenSource.Token;
        using CancellationTokenSource warmUpSource = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task warmUp = Task.CompletedTask;
        try
        {
            if (_logger.IsInfo) _logger.Info($"Starting embedded beacon chain driver. Checkpoint sync URL: {checkpointSync.EffectiveCheckpointSyncUrl}");
            (ForkedBeaconState state, ForkedSignedBeaconBlock? block, Hash256 blockRoot) = persistedAnchor ?? await CheckpointSyncAsync(token);
            Validator[] validators = state switch
            {
                ForkedBeaconState.OfFulu fulu => fulu.State.Validators!,
                ForkedBeaconState.OfGloas gloas => gloas.State.Validators!,
                _ => throw new NotSupportedException($"Unhandled anchor state {state.GetType().Name}"),
            };
            if (block is null)
            {
                InitializePubkeyCache(validators, token);
                if (_logger.IsWarn) _logger.Warn("Anchor block is unavailable (state-file-only bootstrap); the sync orchestrator cannot start.");
                return;
            }

            // The cache build runs behind the first forkchoiceUpdated so the execution layer does not wait for it.
            // The subgroup checks are warmed off the orchestrator worker, so the first epoch of imports does not pay for them.
            await orchestrator.RunAsync(state, block, blockRoot, token, () =>
            {
                InitializePubkeyCache(validators, token);
                warmUp = Task.Run(() => WarmSubgroupChecks(warmUpSource.Token));
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Embedded beacon chain driver failed.", e);
        }
        finally
        {
            // Awaited so StopAsync returns only after the warm-up has left the cache.
            await warmUpSource.CancelAsync();
            await warmUp;
        }
    }

    private void WarmSubgroupChecks(CancellationToken cancellationToken)
    {
        try
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            pubkeyCache.WarmSubgroupChecks(cancellationToken);
            if (_logger.IsInfo) _logger.Info($"Checked the subgroup of {pubkeyCache.Count} cached pubkeys in {stopwatch.Elapsed.TotalSeconds:F1} s");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Warming the pubkey subgroup checks failed.", e);
        }
    }

    /// <returns>The anchor a previous run persisted, or <c>null</c> for a database that has none.</returns>
    private (ForkedBeaconState State, ForkedSignedBeaconBlock? Block, Hash256 BlockRoot)? LoadPersistedAnchor()
    {
        if (!store.TryGetAnchor(out Hash256? anchorRoot, out ulong anchorSlot))
        {
            return null;
        }

        if (_logger.IsInfo) _logger.Info($"Resuming from persisted anchor slot {anchorSlot} (block {anchorRoot})");
        if (!store.TryGetState(anchorRoot, out byte[]? stateSsz))
        {
            throw new InvalidOperationException($"Persisted anchor state {anchorRoot} is missing or corrupt; delete the beaconChain database to checkpoint-sync again.");
        }

        ForkedBeaconState state = BeaconStateCodec.DecodeForked(stateSsz, spec);
        CheckpointSync.ThrowIfWrongNetwork(state, spec);
        CheckpointSync.ThrowIfInvalidSyncCommitteeKeys(state);
        store.TryGetForkedBlock(anchorRoot, out ForkedSignedBeaconBlock? block);
        if (block is not null && (state, block) is not ((ForkedBeaconState.OfFulu, ForkedSignedBeaconBlock.OfFulu) or (ForkedBeaconState.OfGloas, ForkedSignedBeaconBlock.OfGloas)))
        {
            // The importer takes only an anchor state and block of the same fork; checkpoint sync refuses such a pair before it is persisted.
            BeaconFork blockFork = block is ForkedSignedBeaconBlock.OfGloas ? BeaconFork.Gloas : BeaconFork.Fulu;
            throw new InvalidDataException($"The anchor block {anchorRoot} at slot {block.Slot} is a {blockFork} block, but the anchor state at slot {state.Slot} is a {state.Fork} state. Delete the beaconChain database to checkpoint-sync again.");
        }

        return (state, block, anchorRoot);
    }

    private async Task<(ForkedBeaconState State, ForkedSignedBeaconBlock? Block, Hash256 BlockRoot)> CheckpointSyncAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                CheckpointAnchor anchor = await checkpointSync.RunAsync(cancellationToken);
                return (anchor.State, anchor.Block, anchor.BlockRoot);
            }
            catch (Exception e) when (config.CheckpointStateFile is null && CheckpointSync.IsTransientDownloadFailure(e, cancellationToken))
            {
                // Without a driver the execution layer has no consensus client until the process restarts; an unreachable provider is not a reason to stay down.
                if (_logger.IsWarn) _logger.Warn($"Checkpoint sync failed: {CheckpointSync.DescribeCause(e)}; starting it again in {StartRetryDelay.TotalSeconds:F0} s.");
                await Task.Delay(StartRetryDelay, cancellationToken);
            }
        }
    }

    private void InitializePubkeyCache(Validator[] validators, CancellationToken token)
    {
        // A run stopped before the build must not spend seconds on it or persist it.
        token.ThrowIfCancellationRequested();
        Stopwatch stopwatch = Stopwatch.StartNew();
        if (pubkeyCache.TryLoad(store, validators))
        {
            if (_logger.IsInfo) _logger.Info($"Loaded pubkey cache for {pubkeyCache.Count} validators in {stopwatch.Elapsed.TotalSeconds:F1} s");
        }
        else
        {
            if (_logger.IsInfo) _logger.Info($"Building pubkey cache for {validators.Length} validators");
            pubkeyCache.Build(validators);
            pubkeyCache.Persist(store);
            if (_logger.IsInfo) _logger.Info($"Built and persisted pubkey cache for {pubkeyCache.Count} validators in {stopwatch.Elapsed.TotalSeconds:F1} s");
        }
    }

    /// <summary>Permanently stops the driver, e.g. when an external consensus client takes over.</summary>
    public void Stop()
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }

            _cancellationTokenSource.Cancel();
        }
    }

    /// <summary>Stops the driver and awaits its run loop, so <see cref="Dispose"/> only tears the token source down after the driver has actually unwound.</summary>
    public async Task StopAsync()
    {
        Stop();
        if (_runTask is not null)
        {
            await _runTask;
        }
    }

    /// <remarks>Idempotent: the service is disposed both via the plugin dispose stack and as a container-owned singleton.</remarks>
    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }

            // Cancel before the rest of the node tears down, so the driver unwinds from its own token
            // instead of surfacing secondary cancellations (e.g. engine internals going away) as errors.
            _cancellationTokenSource.Cancel();
            _disposed = true;
            externalClDetector.ExternalClDetected -= Stop;
            _cancellationTokenSource.Dispose();
        }
    }
}
