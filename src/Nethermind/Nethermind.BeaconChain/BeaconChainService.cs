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
using Nethermind.Logging;

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
    BeaconChainStore store,
    PubkeyCache pubkeyCache,
    CheckpointSync checkpointSync,
    ExternalClDetector externalClDetector,
    ILogManager logManager) : IDisposable
{
    private readonly ILogger _logger = logManager.GetClassLogger<BeaconChainService>();
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    public async Task Start()
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

            if (_logger.IsInfo) _logger.Info($"Starting embedded beacon chain driver. Checkpoint sync URL: {config.CheckpointSyncUrl}");
            await InitializeAnchorAsync(_cancellationTokenSource.Token);
        }
        catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Embedded beacon chain driver failed.", e);
        }
    }

    private async Task InitializeAnchorAsync(CancellationToken cancellationToken)
    {
        BeaconStateFulu state;
        if (store.TryGetAnchor(out Hash256? anchorRoot, out ulong anchorSlot))
        {
            if (_logger.IsInfo) _logger.Info($"Resuming from persisted anchor slot {anchorSlot} (block {anchorRoot})");
            if (!store.TryGetState(anchorRoot, out byte[]? stateSsz))
            {
                throw new InvalidOperationException($"Persisted anchor state {anchorRoot} is missing or corrupt; delete the beaconChain database to checkpoint-sync again.");
            }

            BeaconStateFulu.Decode(stateSsz, out state);
        }
        else
        {
            state = (await checkpointSync.RunAsync(cancellationToken)).State;
        }

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
    public void Stop() => _cancellationTokenSource.Cancel();

    public void Dispose()
    {
        externalClDetector.ExternalClDetected -= Stop;
        _cancellationTokenSource.Dispose();
    }
}
