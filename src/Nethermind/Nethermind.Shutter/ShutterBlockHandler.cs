// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Shutter.Contracts;
using Nethermind.Logging;
using Nethermind.Abi;
using Nethermind.Blockchain.Receipts;
using System.Threading.Tasks;

namespace Nethermind.Shutter;

// todo: make block handler & receipt folding into more reusable class?
// maybe ReceiptAccumulator with generic log scanning and receipt following
public class ShutterBlockHandler(
    ulong chainId,
    string validatorRegistryContractAddress,
    ulong validatorRegistryMessageVersion,
    ReadOnlyTxProcessingEnvFactory envFactory,
    IAbiEncoder abiEncoder,
    IReceiptFinder receiptFinder,
    Dictionary<ulong, byte[]> validatorsInfo,
    ShutterEon eon,
    ShutterTxLoader txLoader,
    ILogManager logManager) : IShutterBlockHandler
{
    private readonly ILogger _logger = logManager.GetClassLogger();
    private readonly TimeSpan _upToDateCutoff = TimeSpan.FromSeconds(10);
    private bool _haveCheckedRegistered = false;

    public void OnNewHeadBlock(Block head)
    {
        if (IsBlockUpToDate(head))
        {
            // todo: made debug
            _logger.Info($"Shutter block handler {head.Number}");
            if (!_haveCheckedRegistered)
            {
                CheckAllValidatorsRegistered(head.Header, validatorsInfo);
                _haveCheckedRegistered = true;
            }
            eon.Update(head.Header);
            txLoader.LoadFromReceipts(head, receiptFinder.Get(head));
        }
        else
        {
            // todo: made debug
            _logger.Warn($"Shutter block handler not running, outdated block {head.Number}");
        }
    }

    public async Task<(Block?, TxReceipt[])> WaitForBlock(long blockNumber, ulong slot, TimeSpan slotLength, TimeSpan cutoff)
    {
        _logger.Info($"Waiting for block {blockNumber} in {slot}");
        await Task.Delay(100);
        // wait for OnNewHeadBlock
        return (null, []);
    }

    // todo: check if in current slot?
    private bool IsBlockUpToDate(Block head)
        => (head.Header.Timestamp - (ulong)DateTimeOffset.Now.ToUnixTimeSeconds()) < _upToDateCutoff.TotalSeconds;

    private void CheckAllValidatorsRegistered(BlockHeader parent, Dictionary<ulong, byte[]> validatorsInfo)
    {
        if (validatorsInfo.Count == 0)
        {
            return;
        }

        IReadOnlyTxProcessingScope scope = envFactory.Create().Build(parent.StateRoot!);
        ITransactionProcessor processor = scope.TransactionProcessor;

        ValidatorRegistryContract validatorRegistryContract = new(processor, abiEncoder, new(validatorRegistryContractAddress), _logger, chainId, validatorRegistryMessageVersion);
        if (validatorRegistryContract.IsRegistered(parent, validatorsInfo, out HashSet<ulong> unregistered))
        {
            _logger.Info($"All Shutter validator keys are registered.");
        }
        else
        {
            _logger.Error($"Validators not registered to Shutter with the following indices: [{string.Join(", ", unregistered)}]");
        }
    }

}
