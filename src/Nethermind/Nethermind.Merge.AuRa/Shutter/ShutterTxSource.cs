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
using Nethermind.Specs;
using Nethermind.Blockchain;

namespace Nethermind.Merge.AuRa.Shutter;

using LoadedTransactions = ShutterTxLoader.LoadedTransactions;

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
    private LoadedTransactions? _loadedTransactions;
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

        ulong nextSlot = GetNextSlot();

        // atomic fetch
        LoadedTransactions? loadedTransactions = _loadedTransactions;
        if (loadedTransactions is null)
        {
            if (_logger.IsWarn) _logger.Warn($"Decryption keys have not been received, cannot include Shutter transactions.");
        }
        else
        {
            if (_logger.IsInfo) _logger.Info($"Building Shutter block for slot {nextSlot} with {loadedTransactions.Value.Transactions.Length} transactions.");
            if (loadedTransactions.Value.Slot == nextSlot)
            {
                return loadedTransactions.Value.Transactions;
            }

            if (_logger.IsWarn) _logger.Warn($"Decryption keys not received for slot {nextSlot}, cannot include Shutter transactions.");
            if (_logger.IsDebug) _logger.Debug($"Current Shutter decryption keys stored for slot {loadedTransactions.Value.Slot}");
        }

        return [];
    }

    public void LoadTransactions(ulong eon, ulong txPointer, ulong slot, List<(byte[], byte[])> keys)
    {
        _loadedTransactions = _txLoader.LoadTransactions(eon, txPointer, slot, keys);
    }

    public ulong GetLoadedTransactionsSlot() => _loadedTransactions is null ? 0 : _loadedTransactions.Value.Slot;

    private ulong GetNextSlot()
    {
        // assume Gnosis or Chiado chain
        ulong genesisTimestamp = specProvider.ChainId == BlockchainIds.Chiado ? ChiadoSpecProvider.BeaconChainGenesisTimestamp : GnosisSpecProvider.BeaconChainGenesisTimestamp;
        return ((ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - genesisTimestamp) / 5 + 1;
    }

    private bool IsRegistered(BlockHeader parent)
    {
        IReadOnlyTransactionProcessor readOnlyTransactionProcessor = envFactory.Create().Build(parent.StateRoot!);
        ValidatorRegistryContract validatorRegistryContract = new(readOnlyTransactionProcessor, abiEncoder, _validatorRegistryContractAddress, _logger, specProvider.ChainId, _validatorRegistryMessageVersion);
        if (!validatorRegistryContract.IsRegistered(parent, validatorsInfo, out HashSet<ulong> unregistered))
        {
            if (_logger.IsError) _logger.Error($"Validators not registered to Shutter with the following indices: [{string.Join(", ", unregistered)}]");
            return false;
        }
        return true;
    }
}
