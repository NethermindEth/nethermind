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
using Nethermind.Config;
using System.Threading.Tasks;

namespace Nethermind.Merge.AuRa.Shutter;

public class ShutterTxSource(
    ShutterTxLoader txLoader,
    IShutterConfig shutterConfig,
    ISpecProvider specProvider,
    ILogManager logManager)
    : ITxSource
{
    private LruCache<ulong, ShutterTransactions?> _transactionCache = new (10, "Shutter tx cache");
    private readonly ILogger _logger = logManager.GetClassLogger();
    private readonly ulong genesisTimestamp = 1000 * (specProvider.ChainId == BlockchainIds.Chiado ? ChiadoSpecProvider.BeaconChainGenesisTimestamp : GnosisSpecProvider.BeaconChainGenesisTimestamp);
    private const ushort slotLength = 5000;
    private ulong _highestSlotSeen = 0;
    private ulong _extraBuildWindowMs = shutterConfig.ExtraBuildWindow
        == default ? shutterConfig.GetDefaultValue<ulong>(nameof(ShutterConfig.ExtraBuildWindow)) : shutterConfig.ExtraBuildWindow;
    private readonly object _highestSlotSeenLock = new();

    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes = null)
    {
        if (!shutterConfig.Validator)
        {
            if (_logger.IsDebug) _logger.Debug($"Not building Shutter block since running in non-validator mode.");
            return [];
        }

        (ulong buildingSlot, ushort offset) = GetBuildingSlotAndOffset();

        ShutterTransactions? shutterTransactions = _transactionCache.Get(buildingSlot);
        if (shutterTransactions is null)
        {
            WaitForKeysInCurrentSlot(buildingSlot, offset).GetAwaiter().GetResult();
            shutterTransactions = _transactionCache.Get(buildingSlot);
        }
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

    private Task WaitForKeysInCurrentSlot(ulong buildingSlot, ushort timeLeft)
    {
        Task timeout = Task.Delay(timeLeft);
        Task loopCache = Task.Run(() =>
        {
            while (true)
            {
                if (timeout.IsCompleted || _transactionCache.Contains(buildingSlot))
                {
                    return;
                }
                Task.Delay(50).GetAwaiter().GetResult();
            }
        });
        return Task.WhenAny(loopCache, timeout);
    }

    public void LoadTransactions(ulong eon, ulong txPointer, ulong slot, List<(byte[], byte[])> keys)
    {
        _transactionCache.Set(slot, txLoader.LoadTransactions(eon, txPointer, slot, keys));
        lock (_highestSlotSeenLock)
        {
            if (_highestSlotSeen < slot)
                _highestSlotSeen = slot;
        }
    }

    public ulong HighestLoadedSlot() => _highestSlotSeen;

    private (ulong, ushort) GetBuildingSlotAndOffset()
    {
        ulong currentSlot;
        ushort slotOffset;
        ulong timeSinceGenesis = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - genesisTimestamp;
        currentSlot = timeSinceGenesis / slotLength;
        slotOffset = (ushort)(timeSinceGenesis % slotLength);

        // if inside the build window then building for this slot, otherwise next
        return ((slotOffset <= _extraBuildWindowMs) ? currentSlot : currentSlot + 1, slotOffset);
    }
}
