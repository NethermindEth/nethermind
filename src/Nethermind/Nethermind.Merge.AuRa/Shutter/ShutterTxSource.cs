// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

namespace Nethermind.Merge.AuRa.Shutter;

public class ShutterTxSource(
    ILogFinder logFinder,
    IShutterConfig shutterConfig,
    ISpecProvider specProvider,
    IEthereumEcdsa ethereumEcdsa,
    IReadOnlyBlockTree readOnlyBlockTree,
    ILogManager logManager)
    : ITxSource
{
    private ShutterTransactions? _shutterTransactions;
    private readonly ILogger _logger = logManager.GetClassLogger();
    private readonly ShutterTxLoader _txLoader = new(logFinder, shutterConfig, specProvider, ethereumEcdsa, readOnlyBlockTree, logManager);

    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes = null)
    {
        if (!shutterConfig.Validator)
        {
            if (_logger.IsDebug) _logger.Debug($"Not building Shutter block since running in non-validator mode.");
            return [];
        }

        ulong slot = GetBuildingSlot();

        // atomic fetch
        ShutterTransactions? shutterTransactions = _shutterTransactions;
        if (shutterTransactions is null)
        {
            if (_logger.IsWarn) _logger.Warn($"Decryption keys have not been received, cannot include Shutter transactions.");
        }
        else
        {
            int txCount = shutterTransactions.Value.Transactions.Length;
            if (shutterTransactions.Value.Slot == slot)
            {
                if (_logger.IsInfo) _logger.Info($"Building Shutter block for slot {slot} with {txCount} transactions.");
                return shutterTransactions.Value.Transactions;
            }

            if (_logger.IsWarn) _logger.Warn($"Decryption keys not received for slot {slot}, cannot include {txCount} Shutter transactions.");
            if (_logger.IsDebug) _logger.Debug($"Current Shutter decryption keys stored for slot {shutterTransactions.Value.Slot}");
        }

        return [];
    }

    public void LoadTransactions(ulong eon, ulong txPointer, ulong slot, List<(byte[], byte[])> keys)
    {
        _shutterTransactions = _txLoader.LoadTransactions(eon, txPointer, slot, keys);
    }

    public ulong GetLoadedTransactionsSlot() => _shutterTransactions is null ? 0 : _shutterTransactions.Value.Slot;

    private ulong GetBuildingSlot()
    {
        ulong genesisTimestamp = specProvider.TimestampBeaconGenesis!.Value * 1000;
        ulong timeSinceGenesis = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - genesisTimestamp;
        ushort slotLength = (ushort)specProvider.SlotLength!.Value.Milliseconds;
        ulong currentSlot = timeSinceGenesis / slotLength;
        ushort slotOffset = (ushort)(timeSinceGenesis % slotLength);

        // if in first third then building for this slot, otherwise next
        return (slotOffset <= (slotLength / 3)) ? currentSlot : currentSlot + 1;
    }

}
