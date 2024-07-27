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
    private LruCache<ulong, ShutterTransactions?> _transactionCache = new(10, "Shutter tx cache");
    private readonly ILogger _logger = logManager.GetClassLogger();
    private readonly ulong genesisTimestamp = 1000 * (specProvider.ChainId == BlockchainIds.Chiado ? ChiadoSpecProvider.BeaconChainGenesisTimestamp : GnosisSpecProvider.BeaconChainGenesisTimestamp);
    private const ushort slotLength = 5000;
    private ulong _highestSlotSeen = 0;
    private readonly object _syncObject = new();
    private static ConcurrentDictionary<ulong, TaskCompletionSource> _keyWaitTasks = new();

    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes = null)
    {
        if (!shutterConfig.Validator)
        {
            if (_logger.IsDebug) _logger.Debug($"Not building Shutter block since running in non-validator mode.");
            return [];
        }

        ulong buildingSlot = GetBuildingSlotAndOffset(payloadAttributes);

        ShutterTransactions? shutterTransactions = _transactionCache.Get(buildingSlot);

        if (shutterTransactions is null)
        {
            if (_logger.IsWarn)
                _logger.Warn($"No shutter tx could be loaded for slot {buildingSlot}.");
        }
        else
        {
            int txCount = shutterTransactions.Value.Transactions.Length;
            if (_logger.IsInfo) _logger.Info($"Can build for Shutter block slot {buildingSlot} with {txCount} transactions.");
            return shutterTransactions.Value.Transactions;
        }

        return [];
    }
    public Task WaitForTransactions(ulong slot)
    {
        lock (_syncObject)
        {
            if (_highestSlotSeen <= slot)
            {
                return Task.CompletedTask;
            }
            return _keyWaitTasks.GetOrAdd(slot,
               (k) =>
               {
                   TaskCompletionSource tcs = new();
                   //Maximum wait allowed
                   Task.Delay(slotLength)
                   .ContinueWith(t =>
                   {
                       TaskCompletionSource? removed;
                       _keyWaitTasks.TryRemove(slot, out removed);
                       removed?.TrySetCanceled();
                   });
                   return tcs;
               }).Task;
        }
    }

    public void LoadTransactions(ulong eon, ulong txPointer, ulong slot, List<(byte[], byte[])> keys)
    {
        _transactionCache.Set(slot, txLoader.LoadTransactions(eon, txPointer, slot, keys));
        lock (_syncObject)
        {
            if (_highestSlotSeen < slot)
                _highestSlotSeen = slot;
            TaskCompletionSource? tcs;
            if (_keyWaitTasks.Remove(slot, out tcs))
            {
                tcs.TrySetResult();
            }
        }
    }

    public ulong HighestLoadedSlot() => _highestSlotSeen;

    private ulong GetBuildingSlotAndOffset(PayloadAttributes? payloadAttributes)
    {
        var unixTime = payloadAttributes != null ? payloadAttributes.Timestamp : (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ulong timeSinceGenesis = unixTime - genesisTimestamp;
        ulong currentSlot = timeSinceGenesis / slotLength;
        return currentSlot;
    }
}
