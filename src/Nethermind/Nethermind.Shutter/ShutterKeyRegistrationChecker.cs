// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Shutter.Config;
using Nethermind.Shutter.Contracts;

namespace Nethermind.Shutter;

public class ShutterKeyRegistrationChecker
{
    private bool _haveCheckedRegistered = false;
    private readonly ShutterValidatorsInfo _validatorsInfo;
    private readonly ulong _chainId;
    private readonly IShutterConfig _cfg;
    private readonly IBlockTree _blockTree;
    private readonly IShareableTxProcessorSource _txProcessorSource;
    private readonly IAbiEncoder _abiEncoder;
    private readonly ILogManager _logManager;
    private readonly ILogger _logger;

    public ShutterKeyRegistrationChecker(
        ShutterValidatorsInfo validatorsInfo,
        ulong chainId,
        IShutterConfig cfg,
        IBlockTree blockTree,
        IBlockProcessingQueue blockProcessingQueue,
        IShareableTxProcessorSource txProcessorSource,
        IAbiEncoder abiEncoder,
        ILogManager logManager)
    {
        _logger = logManager.GetClassLogger();
        _validatorsInfo = validatorsInfo;
        _chainId = chainId;
        _cfg = cfg;
        _blockTree = blockTree;
        _validatorsInfo = validatorsInfo;
        _abiEncoder = abiEncoder;
        _logManager = logManager;
        _txProcessorSource = txProcessorSource;

        blockProcessingQueue.ProcessingQueueEmpty += OnBlockProcessorQueueEmpty;
    }

    void OnBlockProcessorQueueEmpty(object? sender, EventArgs e)
    {
        if (!_haveCheckedRegistered && _blockTree.Head is not null)
        {
            CheckAllValidatorsRegistered(_blockTree.Head.Header, _validatorsInfo);
            _haveCheckedRegistered = true;
        }
    }

    private void CheckAllValidatorsRegistered(in BlockHeader parent, in ShutterValidatorsInfo validatorsInfo)
    {
        if (validatorsInfo.IsEmpty)
        {
            return;
        }

        using IReadOnlyTxProcessingScope scope = _txProcessorSource.Build(parent);
        ITransactionProcessor processor = scope.TransactionProcessor;

        ValidatorRegistryContract validatorRegistryContract = new(processor, _abiEncoder, new(_cfg.ValidatorRegistryContractAddress!), _logManager, _chainId, _cfg.ValidatorRegistryMessageVersion!);
        if (validatorRegistryContract.IsRegistered(parent, validatorsInfo, out HashSet<ulong> unregistered))
        {
            if (_logger.IsInfo) _logger.Info($"All Shutter validator keys are registered.");
        }
        else if (_logger.IsError)
        {
            _logger.Error($"Validators not registered to Shutter with the following indices: [{string.Join(", ", unregistered)}]");
        }
    }
}