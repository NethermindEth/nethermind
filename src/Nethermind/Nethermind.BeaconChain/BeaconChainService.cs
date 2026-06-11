// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
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
    ILogManager logManager) : IDisposable
{
    private readonly ILogger _logger = logManager.GetClassLogger<BeaconChainService>();
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    public async Task Start()
    {
        try
        {
            if (_logger.IsInfo) _logger.Info($"Starting embedded beacon chain driver. Checkpoint sync URL: {config.CheckpointSyncUrl}");
            await Task.CompletedTask;
        }
        catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Embedded beacon chain driver failed.", e);
        }
    }

    /// <summary>Permanently stops the driver, e.g. when an external consensus client takes over.</summary>
    public void Stop() => _cancellationTokenSource.Cancel();

    public void Dispose() => _cancellationTokenSource.Dispose();
}
