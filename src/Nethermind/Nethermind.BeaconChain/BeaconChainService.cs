// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Engine;
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
    private readonly ILogger _logger = logManager.GetClassLogger<BeaconChainService>();
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Lock _lifecycleLock = new();
    private bool _disposed;
    private Task? _runTask;

    public Task Start()
    {
        _runTask = RunAsync();
        return _runTask;
    }

    private async Task RunAsync()
    {
        try
        {
            externalClDetector.ExternalClDetected += Stop;
            // Detection may have fired before we subscribed.
            if (config.DisableOnExternalCl && externalClDetector.IsExternalClDetected)
            {
                Stop();
                return;
            }

            if (_logger.IsInfo) _logger.Info($"Starting embedded beacon chain driver. Checkpoint sync URL: {checkpointSync.EffectiveCheckpointSyncUrl}");
            store.EnsureSchemaVersion();
            (BeaconStateFulu state, SignedBeaconBlock? block, Hash256 blockRoot) = await InitializeAnchorAsync(_cancellationTokenSource.Token);
            if (block is null)
            {
                if (_logger.IsWarn) _logger.Warn("Anchor block is unavailable (state-file-only bootstrap); the sync orchestrator cannot start.");
                return;
            }

            await orchestrator.RunAsync(state, block, blockRoot, _cancellationTokenSource.Token);
        }
        catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Embedded beacon chain driver failed.", e);
        }
    }

    private async Task<(BeaconStateFulu State, SignedBeaconBlock? Block, Hash256 BlockRoot)> InitializeAnchorAsync(CancellationToken cancellationToken)
    {
        BeaconStateFulu state;
        SignedBeaconBlock? block;
        Hash256 blockRoot;
        if (store.TryGetAnchor(out Hash256? anchorRoot, out ulong anchorSlot))
        {
            if (_logger.IsInfo) _logger.Info($"Resuming from persisted anchor slot {anchorSlot} (block {anchorRoot})");
            if (!store.TryGetState(anchorRoot, out byte[]? stateSsz))
            {
                throw new InvalidOperationException($"Persisted anchor state {anchorRoot} is missing or corrupt; delete the beaconChain database to checkpoint-sync again.");
            }

            state = BeaconStateCodec.Decode(stateSsz, spec);
            blockRoot = anchorRoot;
            store.TryGetBlock(anchorRoot, out block);
        }
        else
        {
            CheckpointAnchor anchor = await checkpointSync.RunAsync(cancellationToken);
            (state, block, blockRoot) = (anchor.State, anchor.Block, anchor.BlockRoot);
        }

        InitializePubkeyCache(state);
        return (state, block, blockRoot);
    }

    private void InitializePubkeyCache(BeaconStateFulu state)
    {
        Validator[] validators = state.Validators!;
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
