// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Logging;
using Nethermind.Blockchain;
using Nethermind.Specs;
using Nethermind.Consensus;
using Nethermind.Core.Crypto;
using System;
using Nethermind.Core.Caching;
using Nethermind.Evm.Tracing.GethStyle.Custom.JavaScript;
using Nethermind.Config;
using System.Threading;

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
    private readonly object _slotSeenLock = new();

    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes = null)
    {
        if (!shutterConfig.Validator)
        {
            if (_logger.IsDebug) _logger.Debug($"Not building Shutter block since running in non-validator mode.");
            return [];
        }

        ulong buildingSlot = GetBuildingSlot();

        // atomic fetch
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

    public void LoadTransactions(ulong eon, ulong txPointer, ulong slot, List<(byte[], byte[])> keys)
    {
        _transactionCache.Set(slot, txLoader.LoadTransactions(eon, txPointer, slot, keys));
        lock (_slotSeenLock)
        {
            if (_highestSlotSeen < slot)
                _highestSlotSeen = slot;
        }
    }

    public ulong HighestLoadedSlot() => _highestSlotSeen;

    private ulong GetBuildingSlot()
    {
        ulong timeSinceGenesis = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - genesisTimestamp;
        ulong currentSlot = timeSinceGenesis / slotLength;
        ushort slotOffset = (ushort)(timeSinceGenesis % slotLength);

        // if inside the build window then building for this slot, otherwise next
        return (slotOffset <= _extraBuildWindowMs) ? currentSlot : currentSlot + 1;
    }

}
