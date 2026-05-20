// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Thread-safe implementation of <see cref="IWitnessCaptureRegistry"/>.
/// Entries are added by the RPC handler thread and removed by the block-processing thread.
/// Under normal operation (serialised <c>newPayload</c> queue) there is at most one
/// armed entry at any point in time.
/// </summary>
public sealed class WitnessCaptureRegistry(
    IStateReader stateReader,
    IHeaderFinder headerFinder,
    ILogManager logManager)
    : IWitnessCaptureRegistry
{
    private readonly ILogger _logger = logManager.GetClassLogger<WitnessCaptureRegistry>();

    private readonly ConcurrentDictionary<Hash256AsKey, TaskCompletionSource<Witness?>> _pending = new();

    public Task<Witness?> ArmCapture(Hash256 blockHash)
    {
        // RunContinuationsAsynchronously: the TCS continuation must not run on the
        // block-processing thread when SetResult is called inside TryDrainCapture.
        TaskCompletionSource<Witness?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pending.TryAdd(blockHash, tcs))
        {
            if (_logger.IsWarn)
                _logger.Warn($"WitnessCaptureRegistry: duplicate ArmCapture for {blockHash}. Replacing previous entry.");
            _pending[blockHash] = tcs;
        }

        return tcs.Task;
    }

    public bool HasPendingCapture(Hash256 blockHash) => _pending.ContainsKey(blockHash);

    public bool TryDrainCapture(Hash256 blockHash, BlockHeader parentHeader, WitnessCapturingWorldStateProxy proxy)
    {
        if (!_pending.TryRemove(blockHash, out TaskCompletionSource<Witness?>? tcs))
            return false;

        Witness? witness = null;
        try
        {
            WitnessGeneratingHeaderFinder perBlockHeaderFinder = new(headerFinder);
            witness = proxy.BuildWitness(parentHeader, stateReader, perBlockHeaderFinder);
        }
        catch (Exception ex)
        {
            if (_logger.IsError)
                _logger.Error($"WitnessCaptureRegistry: witness build failed for block {blockHash}", ex);
        }
        finally
        {
            tcs.SetResult(witness);
        }

        return true;
    }

    public void DisarmCapture(Hash256 blockHash)
    {
        if (_pending.TryRemove(blockHash, out TaskCompletionSource<Witness?>? tcs))
        {
            tcs.TrySetCanceled();

            if (_logger.IsTrace)
                _logger.Trace($"WitnessCaptureRegistry: capture disarmed for {blockHash}");
        }
    }
}
