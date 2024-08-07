// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Merge.AuRa.Shutter.Contracts;
using Nethermind.Logging;
using Nethermind.Abi;
using Nethermind.Core.Caching;

namespace Nethermind.Merge.AuRa.Shutter;

public class ShutterBlockHandler(
    ulong chainId,
    string validatorRegistryContractAddress,
    ulong validatorRegistryMessageVersion,
    ReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory, IAbiEncoder abiEncoder,
    Dictionary<ulong, byte[]> validatorsInfo,
    ShutterEon eon,
    ShutterTxLoader txLoader,
    ILogManager logManager) : IShutterBlockHandler
{
    private readonly ILogger _logger = logManager.GetClassLogger();
    private readonly TimeSpan _upToDateCutoff = TimeSpan.FromSeconds(10);
    private bool _haveCheckedRegistered = false;
    private readonly LruCache<Core.Crypto.Hash256, TxReceipt[]> _receiptsCache = new(10, "Receipts cache");

    public void OnNewHeadBlock(Block head)
    {
        if (IsBlockUpToDate(head))
        {
            if (!_haveCheckedRegistered)
            {
                CheckRegistered(head.Header, validatorsInfo, readOnlyTxProcessingEnvFactory);
                _haveCheckedRegistered = true;
            }
            eon.Update(head.Header);

            if (head.Hash is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Head block hash was null, cannot load Shutter transactions.");
                return;
            }

            if (!_receiptsCache.TryGet(head.Hash, out TxReceipt[] receipts))
            {
                if (_logger.IsWarn) _logger.Warn($"Could not find receipts in cache for new head block ({head.Hash}), cannot load Shutter events.");
                return;
            }

            txLoader.LoadFromReceipts(head, receipts);
        }
    }

    public void OnBlockProcessed(Block block, TxReceipt[] receipts)
    {
        if (IsBlockUpToDate(block) && block.Hash is not null)
        {
            _receiptsCache.Set(block.Hash, receipts);
        }
    }

    private bool IsBlockUpToDate(Block head)
        => (head.Header.Timestamp - (ulong)DateTimeOffset.Now.ToUnixTimeSeconds()) < _upToDateCutoff.TotalSeconds;

    private void CheckRegistered(BlockHeader parent, Dictionary<ulong, byte[]> validatorsInfo, ReadOnlyTxProcessingEnvFactory envFactory)
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
            if (_logger.IsInfo) _logger.Info($"All Shutter validators are registered.");
        }
        else
        {
            if (_logger.IsError) _logger.Error($"Validators not registered to Shutter with the following indices: [{string.Join(", ", unregistered)}]");
        }
    }

}
