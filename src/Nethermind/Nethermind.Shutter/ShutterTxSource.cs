// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.AuRa.Config;
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
    private readonly LruCache<ulong, ShutterTransactions?> _transactionCache = new(5, "Shutter tx cache");
    private readonly ILogger _logger = logManager.GetClassLogger();
    private readonly ulong _genesisTimestampMs = ShutterHelpers.GetGenesisTimestampMs(specProvider);
    private ulong _highestLoadedSlot = 0;
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource> _keyWaitTasks = new();

    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes = null)
    {
        if (!shutterConfig.Validator)
        {
            if (_logger.IsDebug) _logger.Debug($"Not building Shutter block since running in non-validator mode.");
            return [];
        }

        if (payloadAttributes is null)
        {
            if (_logger.IsError) _logger.Error($"Not building Shutter block since payload attributes was null.");
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

        ShutterTransactions? shutterTransactions = _transactionCache.Get(buildingSlot);
        if (shutterTransactions is null)
        {
            if (_logger.IsDebug) _logger.Debug($"No Shutter transactions currently loaded for slot {buildingSlot}.");
            return [];
        }

        int txCount = shutterTransactions.Value.Transactions.Length;
        if (_logger.IsInfo) _logger.Info($"Preparing Shutter block for slot {buildingSlot} with {txCount} transactions.");
        return shutterTransactions.Value.Transactions;
    }

    public async Task WaitForTransactions(ulong slot, CancellationToken cancellationToken)
    {
        if (_transactionCache.Contains(slot))
        {
            return;
        }

        using (cancellationToken.Register(() => CancelWaitForTransactions(slot)))
        {
            await _keyWaitTasks.GetOrAdd(slot, _ => new()).Task;
        }
    }

    public void LoadTransactions(ulong eon, ulong txPointer, ulong slot, List<(byte[], byte[])> keys)
    {
        _transactionCache.Set(slot, txLoader.LoadTransactions(eon, txPointer, slot, keys));

        if (_highestLoadedSlot < slot)
        {
            _highestLoadedSlot = slot;
        }

        if (_keyWaitTasks.Remove(slot, out TaskCompletionSource? tcs))
        {
            tcs?.TrySetResult();
        }
    }

    public ulong HighestLoadedSlot() => _highestLoadedSlot;

    private void CancelWaitForTransactions(ulong slot)
    {
        _keyWaitTasks.Remove(slot, out TaskCompletionSource? cancelledWaitTask);
        cancelledWaitTask?.TrySetException(new OperationCanceledException());
    }
}
