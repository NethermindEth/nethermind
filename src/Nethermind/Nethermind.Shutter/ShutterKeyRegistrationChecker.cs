// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Shutter.Config;
using Nethermind.Shutter.Contracts;

namespace Nethermind.Shutter;

public class ShutterKeyRegistrationChecker
{
    private bool _isRegistered = false;
    private readonly ShutterValidatorsInfo _validatorsInfo;
    private readonly ulong _chainId;
    private readonly IShutterConfig _cfg;
    private readonly IBlockTree _blockTree;
    private readonly IBackgroundTaskScheduler _backgroundTaskScheduler;
    private readonly IShareableTxProcessorSource _txProcessorSource;
    private readonly IAbiEncoder _abiEncoder;
    private readonly ILogManager _logManager;
    private readonly ILogger _logger;
    private readonly IProcessExitSource _processExitSource;
    private readonly Lock _registrationCheckLock = new();

    public ShutterKeyRegistrationChecker(
        ShutterValidatorsInfo validatorsInfo,
        ulong chainId,
        IShutterConfig cfg,
        IBlockTree blockTree,
        IBlockProcessingQueue blockProcessingQueue,
        IBackgroundTaskScheduler backgroundTaskScheduler,
        IProcessExitSource processExitSource,
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
        _backgroundTaskScheduler = backgroundTaskScheduler;
        _processExitSource = processExitSource;

        blockProcessingQueue.ProcessingQueueEmpty += OnBlockProcessorQueueEmpty;
    }

    void OnBlockProcessorQueueEmpty(object? sender, EventArgs e)
    {
        _backgroundTaskScheduler.ScheduleTask(1, (_, cancellationToken) => {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _processExitSource.Token);
            return CheckAllValidatorsRegistered(_, cts.Token);
        });
    }

    private Task CheckAllValidatorsRegistered(int _, CancellationToken cancellationToken)
    {
        lock (_registrationCheckLock)
        {
            if (_isRegistered || _blockTree.Head is null || _validatorsInfo.IsEmpty)
            {
                return Task.CompletedTask;
            }

            BlockHeader parent = _blockTree.Head.Header;

            using IReadOnlyTxProcessingScope scope = _txProcessorSource.Build(parent);
            ITransactionProcessor processor = scope.TransactionProcessor;

            ValidatorRegistryContract validatorRegistryContract = new(processor, _abiEncoder, new(_cfg.ValidatorRegistryContractAddress!), _logManager, _chainId, _cfg.ValidatorRegistryMessageVersion!);
            if (validatorRegistryContract.IsRegistered(parent, _validatorsInfo, out HashSet<ulong> unregistered, cancellationToken))
            {
                if (_logger.IsInfo) _logger.Info($"All Shutter validator keys are registered.");
                _isRegistered = true;
            }
            else if (_logger.IsError && !cancellationToken.IsCancellationRequested)
            {
                _logger.Error($"Validators not registered to Shutter with the following indices: [{string.Join(", ", unregistered)}]");
            }
        }

        return Task.CompletedTask;
    }
}