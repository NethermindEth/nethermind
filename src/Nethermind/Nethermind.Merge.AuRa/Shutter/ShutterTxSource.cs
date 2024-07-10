// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Abi;
using Nethermind.Crypto;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Producers;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Logging;
using Nethermind.Consensus.Processing;
using Nethermind.Merge.AuRa.Shutter.Contracts;
using Nethermind.Blockchain;

namespace Nethermind.Merge.AuRa.Shutter;

public class ShutterTxSource(
    ILogFinder logFinder,
    ReadOnlyTxProcessingEnvFactory envFactory,
    IAbiEncoder abiEncoder,
    IShutterConfig shutterConfig,
    ISpecProvider specProvider,
    IEthereumEcdsa ethereumEcdsa,
    IReadOnlyBlockTree readOnlyBlockTree,
    Dictionary<ulong, byte[]> validatorsInfo,
    ILogManager logManager)
    : ITxSource
{
    private ShutterTransactions? _shutterTransactions;
    private bool _validatorsRegistered;
    private readonly ILogger _logger = logManager.GetClassLogger();
    private readonly ShutterTxLoader _txLoader = new(logFinder, shutterConfig, specProvider, ethereumEcdsa, readOnlyBlockTree, logManager);
    private readonly Address _validatorRegistryContractAddress = new(shutterConfig.ValidatorRegistryContractAddress!);
    private readonly ulong _validatorRegistryMessageVersion = shutterConfig.ValidatorRegistryMessageVersion;

    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes = null)
    {
        if (!shutterConfig.Validator)
        {
            if (_logger.IsDebug) _logger.Debug($"Not building Shutter block since running in non-validator mode.");
            return [];
        }

        // assume validator will stay registered
        if (!_validatorsRegistered)
        {
            if (!IsRegistered(parent))
            {
                return [];
            }

            _validatorsRegistered = true;
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
            if (_logger.IsInfo) _logger.Info($"Building Shutter block for slot {slot} with {shutterTransactions.Value.Transactions.Length} transactions.");
            if (shutterTransactions.Value.Slot == slot)
            {
                return shutterTransactions.Value.Transactions;
            }

            if (_logger.IsWarn) _logger.Warn($"Decryption keys not received for slot {slot}, cannot include Shutter transactions.");
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

    private bool IsRegistered(BlockHeader parent)
    {
        IReadOnlyTxProcessingScope scope = envFactory.Create().Build(parent.StateRoot!);
        ITransactionProcessor processor = scope.TransactionProcessor;

        ValidatorRegistryContract validatorRegistryContract = new(processor, abiEncoder, _validatorRegistryContractAddress, _logger, specProvider.ChainId, _validatorRegistryMessageVersion);
        if (!validatorRegistryContract.IsRegistered(parent, validatorsInfo, out HashSet<ulong> unregistered))
        {
            if (_logger.IsError) _logger.Error($"Validators not registered to Shutter with the following indices: [{string.Join(", ", unregistered)}]");
            return false;
        }
        return true;
    }
}
