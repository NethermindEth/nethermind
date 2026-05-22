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
/// Thread-safe registry. Multiple armed entries for distinct block hashes coexist;
/// a duplicate <see cref="ArmCapture"/> cancels the prior TCS and replaces it.
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
        // RunContinuationsAsynchronously: TryDrainCapture's SetResult must not run the
        // handler's continuation on the block-processing thread.
        TaskCompletionSource<Witness?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource<Witness?> effectiveTcs = _pending.AddOrUpdate(
            blockHash,
            tcs,
            (_, existingTcs) =>
            {
                if (_logger.IsWarn)
                    _logger.Warn($"WitnessCaptureRegistry: duplicate ArmCapture for {blockHash}. Replacing previous entry.");
                existingTcs.TrySetCanceled();
                return tcs;
            });

        return effectiveTcs.Task;
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
