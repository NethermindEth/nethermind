// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Logging;
using Nethermind.Specs;
using System;
using Nethermind.Core.Caching;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace Nethermind.Merge.AuRa.Shutter;

public class ShutterTxSource(
    ShutterTxLoader txLoader,
    IShutterConfig shutterConfig,
    ISpecProvider specProvider,
    ILogManager logManager)
    : ITxSource, IShutterTxSignal
{
    private readonly LruCache<ulong, ShutterTransactions?> _transactionCache = new(5, "Shutter tx cache");
    private readonly ILogger _logger = logManager.GetClassLogger();
    private readonly ulong _genesisTimestampMs = 1000 * (specProvider.ChainId == BlockchainIds.Chiado ? ChiadoSpecProvider.BeaconChainGenesisTimestamp : GnosisSpecProvider.BeaconChainGenesisTimestamp);
    private readonly TimeSpan _slotLength = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _keyWaitTimeout = TimeSpan.FromSeconds(10);
    private ulong _highestLoadedSlot = 0;
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource> _keyWaitTasks = new();
    private readonly object _syncObject = new();

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

        ulong buildingSlot = ShutterHelpers.GetBuildingSlotAndOffset(payloadAttributes!.Timestamp * 1000, _genesisTimestampMs, _slotLength).slot;

        ShutterTransactions? shutterTransactions = _transactionCache.Get(buildingSlot);
        if (shutterTransactions is null)
        {
            return [];
        }

        int txCount = shutterTransactions.Value.Transactions.Length;
        if (_logger.IsInfo) _logger.Info($"Preparing Shutter block for slot {buildingSlot} with {txCount} transactions.");
        return shutterTransactions.Value.Transactions;
    }

    public Task WaitForTransactions(ulong slot)
    {
        lock (_syncObject)
        {
            if (_highestLoadedSlot <= slot)
            {
                return Task.CompletedTask;
            }

            return _keyWaitTasks.GetOrAdd(slot, _ =>
            {
                // maximum wait allowed
                Task.Delay(_keyWaitTimeout).ContinueWith(_ =>
                {
                    _keyWaitTasks.TryRemove(slot, out TaskCompletionSource? removed);
                    removed?.TrySetCanceled();
                }).Start();

                return new();
            }).Task;
        }
    }

    public void LoadTransactions(ulong eon, ulong txPointer, ulong slot, List<(byte[], byte[])> keys)
    {
        _transactionCache.Set(slot, txLoader.LoadTransactions(eon, txPointer, slot, keys));

        lock (_syncObject)
        {
            if (_highestLoadedSlot < slot)
            {
                _highestLoadedSlot = slot;
            }

            if (_keyWaitTasks.Remove(slot, out TaskCompletionSource? tcs))
            {
                tcs?.TrySetResult();
            }
        }
    }

    public ulong HighestLoadedSlot() => _highestLoadedSlot;
}
