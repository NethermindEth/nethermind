// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Abi;
using Nethermind.Crypto;
using Nethermind.Blockchain.Filters;
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

public class ShutterTxSource : ITxSource
{
    private LoadedTransactions _loadedTransactions;
    private bool _validatorsRegistered;
    private readonly ReadOnlyTxProcessingEnvFactory _envFactory;
    private readonly IAbiEncoder _abiEncoder;
    private readonly ISpecProvider _specProvider;
    private readonly ILogger _logger;
    private readonly ShutterTxLoader _txLoader;
    private readonly Address _validatorRegistryContractAddress;
    private readonly Dictionary<ulong, byte[]> _validatorsInfo;
    private readonly ulong _validatorRegistryMessageVersion;

    public ShutterTxSource(ILogFinder logFinder,
        IFilterStore filterStore,
        ReadOnlyTxProcessingEnvFactory envFactory,
        IAbiEncoder abiEncoder,
        IShutterConfig shutterConfig,
        ISpecProvider specProvider,
        IEthereumEcdsa ethereumEcdsa,
        IReadOnlyBlockTree readOnlyBlockTree,
        Dictionary<ulong, byte[]> validatorsInfo,
        ILogManager logManager)
    {
        _envFactory = envFactory;
        _abiEncoder = abiEncoder;
        _specProvider = specProvider;
        _logger = logManager.GetClassLogger();
        _txLoader = new(logFinder, filterStore, shutterConfig, specProvider, ethereumEcdsa, readOnlyBlockTree, logManager);
        _validatorRegistryContractAddress = new(shutterConfig.ValidatorRegistryContractAddress);
        _validatorsInfo = validatorsInfo;
        _validatorRegistryMessageVersion = shutterConfig.ValidatorRegistryMessageVersion;
    }

    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes = null)
    {
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
        LoadedTransactions loadedTransactions = _loadedTransactions;
        if (loadedTransactions.Slot == nextSlot)
        {
            return loadedTransactions.Transactions;
        }

        if (_logger.IsWarn) _logger.Warn($"Decryption keys not received for slot {nextSlot}, cannot include Shutter transactions.");
        if (_logger.IsDebug) _logger.Debug($"Current Shutter decryption keys stored for slot {loadedTransactions.Slot}");
        return [];
    }

    public void LoadTransactions(ulong eon, ulong txPointer, ulong slot, List<(byte[], byte[])> keys)
    {
        _loadedTransactions = _txLoader.LoadTransactions(eon, txPointer, slot, keys);
    }

    public ulong GetLoadedTransactionsSlot()
    {
        return _loadedTransactions.Slot;
    }

    private ulong GetNextSlot()
    {
        // assume Gnosis or Chiado chain
        ulong genesisTimestamp = (_specProvider.ChainId == BlockchainIds.Chiado) ? ChiadoSpecProvider.BeaconChainGenesisTimestamp : GnosisSpecProvider.BeaconChainGenesisTimestamp;
        return (((ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - genesisTimestamp) / 5) + 1;
    }

    private bool IsRegistered(BlockHeader parent)
    {
        IReadOnlyTransactionProcessor readOnlyTransactionProcessor = _envFactory.Create().Build(parent.StateRoot!);
        ValidatorRegistryContract validatorRegistryContract = new(readOnlyTransactionProcessor, _abiEncoder, _validatorRegistryContractAddress, _logger, _specProvider.ChainId, _validatorRegistryMessageVersion);
        if (!validatorRegistryContract!.IsRegistered(parent, _validatorsInfo, out HashSet<ulong> unregistered))
        {
            if (_logger.IsError) _logger.Error($"Validators not registered to Shutter with the following indices: [{string.Join(", ", unregistered)}]");
            return false;
        }
        return true;
    }
}
