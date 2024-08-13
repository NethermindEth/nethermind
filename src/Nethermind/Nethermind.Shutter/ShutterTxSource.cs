// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Consensus.Producers;
using Nethermind.Shutter.Config;
using Nethermind.Logging;
using System;
using Nethermind.Core.Caching;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Threading;

namespace Nethermind.Shutter;

public class ShutterTxSource(
    ShutterTxLoader txLoader,
    IShutterConfig shutterConfig,
    ISpecProvider specProvider,
    ILogManager logManager)
    : ITxSource, IShutterTxSignal
{
    private readonly LruCache<ulong, ShutterTransactions?> _txCache = new(5, "Shutter tx cache");
    private readonly ILogger _logger = logManager.GetClassLogger();
    private readonly ulong _genesisTimestampMs = ShutterHelpers.GetGenesisTimestampMs(specProvider);
    private ulong _highestLoadedSlot = 0;
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource> _keyWaitTasks = new();
    private readonly object _syncObject = new();

    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes = null)
    {
        // todo: tmp
        _logger.Info($"Shutter tx source");

        if (!shutterConfig.Validator)
        {
            _logger.Debug($"Not building Shutterized block since running in non-validator mode.");
            return [];
        }

        if (payloadAttributes is null)
        {
            _logger.Error($"Not building Shutterized block since payload attributes was null.");
        }

        ulong buildingSlot;
        try
        {
            (buildingSlot, long offset) = ShutterHelpers.GetBuildingSlotAndOffset(payloadAttributes!.Timestamp * 1000, _genesisTimestampMs);
        }
        catch (ShutterHelpers.ShutterSlotCalulationException e)
        {
            if (_logger.IsDebug) _logger.Warn($"Could not calculate Shutter building slot: {e}");
            return [];
        }

        ShutterTransactions? shutterTransactions = _txCache.Get(buildingSlot);
        if (shutterTransactions is null)
        {
            _logger.Debug($"No Shutter transactions currently loaded for slot {buildingSlot}.");
            return [];
        }

        int txCount = shutterTransactions.Value.Transactions.Length;
        _logger.Info($"Preparing Shutterized block {parent.Number + 1} for slot {buildingSlot} with {txCount} decrypted transactions.");
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

            using (cancellationToken.Register(() => CancelWaitForTransactions(slot)))
            {
                tcs = _keyWaitTasks.GetOrAdd(slot, _ => new());
            }
        }
        await tcs.Task;
    }

    public void LoadTransactions(Block? head, IShutterMessageHandler.ValidatedKeyArgs keys)
    {
        _txCache.Set(keys.Slot, txLoader.LoadTransactions(head, keys));

        if (_highestLoadedSlot < keys.Slot)
        {
            _highestLoadedSlot = keys.Slot;
        }

        lock (_syncObject)
        {
            if (_keyWaitTasks.Remove(keys.Slot, out TaskCompletionSource? tcs))
            {
                tcs?.TrySetResult();
            }
        }
    }

    public ulong HighestLoadedSlot() => _highestLoadedSlot;

    private void CancelWaitForTransactions(ulong slot)
    {
        _keyWaitTasks.Remove(slot, out TaskCompletionSource? cancelledWaitTask);
        cancelledWaitTask?.TrySetException(new OperationCanceledException());
    }
}
