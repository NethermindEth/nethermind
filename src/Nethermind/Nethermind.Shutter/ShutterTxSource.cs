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
using System.Threading;
using Nethermind.Core.Collections;

namespace Nethermind.Shutter;

public class ShutterTxSource(
    ShutterTxLoader txLoader,
    IShutterConfig shutterConfig,
    SlotTime slotTime,
    ILogManager logManager)
    : ITxSource, IShutterTxSignal
{
    private readonly LruCache<ulong, ShutterTransactions?> _txCache = new(5, "Shutter tx cache");
    private readonly ILogger _logger = logManager.GetClassLogger();
    private ulong _keyWaitTaskId = 0;
    private readonly Dictionary<ulong, Dictionary<ulong, (TaskCompletionSource, CancellationTokenRegistration)>> _keyWaitTasks = [];
    private readonly Lock _syncObject = new();

    public bool SupportsBlobs => false;

    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes = null, bool filterSource = false)
    {
        if (!shutterConfig.Validator)
        {
            if (_logger.IsDebug) _logger.Debug($"Not building Shutterized block since running in non-validator mode.");
            return [];
        }

        ulong buildingSlot;
        try
        {
            (buildingSlot, _) = slotTime.GetBuildingSlotAndOffset(payloadAttributes!.Timestamp * 1000);
        }
        catch (SlotTime.SlotCalulationException e)
        {
            if (_logger.IsDebug) _logger.Warn($"DEBUG/ERROR Could not calculate Shutter building slot: {e}");
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

    public Task WaitForTransactions(ulong slot, CancellationToken cancellationToken)
    {
        TaskCompletionSource? tcs = null;
        lock (_syncObject)
        {
            if (_txCache.Contains(slot))
            {
                return Task.CompletedTask;
            }

            ulong taskId = _keyWaitTaskId++;
            tcs = new();
            CancellationTokenRegistration ctr = cancellationToken.Register(() => CancelWaitForTransactions(slot, taskId));

            if (!_keyWaitTasks.ContainsKey(slot))
            {
                _keyWaitTasks.Add(slot, []);
            }
            Dictionary<ulong, (TaskCompletionSource, CancellationTokenRegistration)> slotWaitTasks = _keyWaitTasks.GetValueOrDefault(slot)!;
            slotWaitTasks!.Add(taskId, (tcs, ctr));
        }
        return tcs.Task;
    }

    private void CancelWaitForTransactions(ulong slot, ulong taskId)
    {
        lock (_syncObject)
        {
            if (_keyWaitTasks.TryGetValue(slot, out Dictionary<ulong, (TaskCompletionSource, CancellationTokenRegistration)>? slotWaitTasks))
            {
                if (slotWaitTasks.TryGetValue(taskId, out (TaskCompletionSource Tcs, CancellationTokenRegistration Ctr) waitTask))
                {
                    waitTask.Tcs.TrySetException(new OperationCanceledException());
                    waitTask.Ctr.Dispose();
                }
                slotWaitTasks.Remove(taskId);
            }
        }
    }

    public bool HaveTransactionsArrived(ulong slot)
    {
        return _txCache.Contains(slot);
    }

    public ShutterTransactions LoadTransactions(Block? head, BlockHeader parentHeader, IShutterKeyValidator.ValidatedKeys keys)
    {
        ShutterTransactions transactions = txLoader.LoadTransactions(head, parentHeader, keys);
        _txCache.Set(keys.Slot, transactions);

        lock (_syncObject)
        {
            if (_keyWaitTasks.Remove(keys.Slot, out Dictionary<ulong, (TaskCompletionSource Tcs, CancellationTokenRegistration Ctr)>? slotWaitTasks))
            {
                slotWaitTasks.ForEach(waitTask =>
                {
                    waitTask.Value.Tcs.TrySetResult();
                    waitTask.Value.Ctr.Dispose();
                });
            }
        }

        return transactions;
    }

    public void Dispose()
    {
        lock (_syncObject)
        {
            _keyWaitTasks.ForEach(static x => x.Value.ForEach(static waitTask => waitTask.Value.Item2.Dispose()));
        }
    }
}
