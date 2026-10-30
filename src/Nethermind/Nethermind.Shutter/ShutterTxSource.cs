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
    private readonly ILogger _logger = logManager.GetClassLogger<ShutterTxSource>();
    private ulong _keyWaitTaskId = 0;
    private readonly Dictionary<ulong, Dictionary<ulong, (TaskCompletionSource, CancellationTokenRegistration)>> _keyWaitTasks = [];
    private readonly Lock _syncObject = new();

    public bool SupportsBlobs => false;

    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, BlockHeader targetBlock, ulong gasLimit, PayloadAttributes? payloadAttributes = null, bool filterSource = false)
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
        catch (SlotTime.SlotCalculationException e)
        {
            _logger.DebugWarn($"Could not calculate Shutter building slot: {e}");
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
        CancellationTokenRegistration ctr;
        lock (_syncObject)
        {
            if (_txCache.Contains(slot))
            {
                return Task.CompletedTask;
            }

            ulong taskId = _keyWaitTaskId++;
            tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ctr = cancellationToken.Register(() => CancelWaitForTransactions(slot, taskId));

            // A token cancelled before Register runs the callback inline, before the wait below is stored, so it finds
            // nothing to cancel; storing the wait then would leave it pending forever.
            if (!cancellationToken.IsCancellationRequested)
            {
                if (!_keyWaitTasks.ContainsKey(slot))
                {
                    _keyWaitTasks.Add(slot, []);
                }
                Dictionary<ulong, (TaskCompletionSource, CancellationTokenRegistration)> slotWaitTasks = _keyWaitTasks.GetValueOrDefault(slot)!;
                slotWaitTasks!.Add(taskId, (tcs, ctr));
                return tcs.Task;
            }
        }

        ctr.Dispose();
        return Task.FromException(new OperationCanceledException(cancellationToken));
    }

    // Registrations are disposed outside _syncObject: CancellationTokenRegistration.Dispose blocks until an
    // in-flight callback returns, and that callback (CancelWaitForTransactions) needs the same lock.
    private void CancelWaitForTransactions(ulong slot, ulong taskId)
    {
        (TaskCompletionSource Tcs, CancellationTokenRegistration Ctr) waitTask;
        lock (_syncObject)
        {
            if (!_keyWaitTasks.TryGetValue(slot, out Dictionary<ulong, (TaskCompletionSource, CancellationTokenRegistration)>? slotWaitTasks)
                || !slotWaitTasks.Remove(taskId, out waitTask))
            {
                return;
            }
        }

        waitTask.Tcs.TrySetException(new OperationCanceledException());
        waitTask.Ctr.Dispose();
    }

    public bool HaveTransactionsArrived(ulong slot) => _txCache.Contains(slot);

    public ShutterTransactions LoadTransactions(Block? head, BlockHeader parentHeader, IShutterKeyValidator.ValidatedKeys keys)
    {
        ShutterTransactions transactions = txLoader.LoadTransactions(head, parentHeader, keys);
        _txCache.Set(keys.Slot, transactions);

        Dictionary<ulong, (TaskCompletionSource Tcs, CancellationTokenRegistration Ctr)>? slotWaitTasks;
        lock (_syncObject)
        {
            _keyWaitTasks.Remove(keys.Slot, out slotWaitTasks);
        }

        slotWaitTasks?.ForEach(waitTask =>
        {
            waitTask.Value.Tcs.TrySetResult();
            waitTask.Value.Ctr.Dispose();
        });

        return transactions;
    }

    public void Dispose()
    {
        List<CancellationTokenRegistration> registrations = [];
        lock (_syncObject)
        {
            foreach (Dictionary<ulong, (TaskCompletionSource, CancellationTokenRegistration)> slotWaitTasks in _keyWaitTasks.Values)
            {
                foreach ((TaskCompletionSource _, CancellationTokenRegistration ctr) in slotWaitTasks.Values)
                {
                    registrations.Add(ctr);
                }
            }

            _keyWaitTasks.Clear();
        }

        foreach (CancellationTokenRegistration ctr in registrations)
        {
            ctr.Dispose();
        }
    }
}
