// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Consensus.Producers;
using Nethermind.Shutter.Config;
using Nethermind.Logging;
using System;
using Nethermind.Core.Caching;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Threading;
using Nethermind.Core.Collections;

namespace Nethermind.Shutter;

public class ShutterTxSource(
    ShutterTxLoader txLoader,
    IShutterConfig shutterConfig,
    ShutterTime shutterTime,
    ILogManager logManager)
    : ITxSource, IShutterTxSignal
{
    private readonly LruCache<ulong, ShutterTransactions?> _txCache = new(5, "Shutter tx cache");
    private readonly ILogger _logger = logManager.GetClassLogger();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource> _keyWaitTasks = new();
    private readonly object _syncObject = new();
    private readonly ArrayPoolList<CancellationTokenRegistration> _ctr = new(5);

    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes = null)
    {
        if (!shutterConfig.Validator)
        {
            if (_logger.IsDebug) _logger.Debug($"Not building Shutterized block since running in non-validator mode.");
            return [];
        }

        if (payloadAttributes is null)
        {
            if (_logger.IsError) _logger.Error($"Not building Shutterized block since payload attributes was null.");
            return [];
        }

        ulong buildingSlot;
        try
        {
            (buildingSlot, long offset) = shutterTime.GetBuildingSlotAndOffset(payloadAttributes!.Timestamp * 1000);
        }
        catch (ShutterTime.ShutterSlotCalulationException e)
        {
            if (_logger.IsDebug) _logger.Warn($"Could not calculate Shutter building slot: {e}");
            return [];
        }

        ShutterTransactions? shutterTransactions = _txCache.Get(buildingSlot);
        if (shutterTransactions is null)
        {
            if (_logger.IsDebug) _logger.Debug($"No Shutter transactions currently loaded for slot {buildingSlot}.");
            return [];
        }

        int txCount = shutterTransactions.Value.Transactions.Length;
        if (_logger.IsInfo) _logger.Info($"Preparing Shutterized block {parent.Number + 1} for slot {buildingSlot} with {txCount} decrypted transactions.");
        return shutterTransactions.Value.Transactions;
    }

    public async Task WaitForTransactions(ulong slot, CancellationToken cancellationToken)
    {
        TaskCompletionSource? tcs = null;
        lock (_syncObject)
        {
            if (_txCache.Contains(slot))
            {
                return;
            }

            _ctr.Add(cancellationToken.Register(() => CancelWaitForTransactions(slot)));
            tcs = _keyWaitTasks.GetOrAdd(slot, _ => new());
        }
        await tcs.Task;
    }

    public bool HaveTransactionsArrived(ulong slot)
    {
        return _txCache.Contains(slot);
    }

    public void LoadTransactions(Block? head, BlockHeader parentHeader, IShutterKeyValidator.ValidatedKeyArgs keys)
    {
        _txCache.Set(keys.Slot, txLoader.LoadTransactions(head, parentHeader, keys));

        lock (_syncObject)
        {
            if (_keyWaitTasks.Remove(keys.Slot, out TaskCompletionSource? tcs))
            {
                tcs?.TrySetResult();
            }
        }
    }

    private void CancelWaitForTransactions(ulong slot)
    {
        _keyWaitTasks.Remove(slot, out TaskCompletionSource? waitTask);
        waitTask?.TrySetException(new OperationCanceledException());
    }

    public void Dispose()
    {
        foreach (CancellationTokenRegistration ctr in _ctr)
        {
            ctr.Dispose();
        }
    }
}
