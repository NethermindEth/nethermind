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
using System.Threading;
using System.Collections.Concurrent;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Blockchain;
using Nethermind.Core.Collections;
using Org.BouncyCastle.Crypto.Prng.Drbg;

namespace Nethermind.Shutter;

public class ShutterBlockHandler(
    ulong chainId,
    string validatorRegistryContractAddress,
    ulong validatorRegistryMessageVersion,
    ReadOnlyTxProcessingEnvFactory envFactory,
    IReadOnlyBlockTree blockTree,
    IAbiEncoder abiEncoder,
    IReceiptFinder receiptFinder,
    Dictionary<ulong, byte[]> validatorsInfo,
    IShutterEon eon,
    ShutterTxLoader txLoader,
    ShutterTime shutterTime,
    ILogManager logManager) : IShutterBlockHandler
{
    private readonly ArrayPoolList<CancellationTokenSource> _cts = new(100);
    private readonly ArrayPoolList<CancellationTokenRegistration> _ctr = new(50);
    private readonly ILogger _logger = logManager.GetClassLogger();
    private bool _haveCheckedRegistered = false;
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<Block?>> _blockWaitTasks = new();
    private readonly LruCache<ulong, Hash256?> _slotToBlockHash = new(5, "Slot to block hash mapping");
    private readonly object _syncObject = new();
    private readonly ulong _genesisTimestampMs = shutterTime.GetGenesisTimestampMs();

    public void OnNewHeadBlock(Block head)
    {
        if (shutterTime.IsBlockUpToDate(head))
        {
            _logger.Debug($"Shutter block handler {head.Number}");

            if (!_haveCheckedRegistered)
            {
                CheckAllValidatorsRegistered(head.Header, validatorsInfo);
                _haveCheckedRegistered = true;
            }
            eon.Update(head.Header);
            txLoader.LoadFromReceipts(head, receiptFinder.Get(head));

            lock (_syncObject)
            {
                ulong slot = shutterTime.GetSlot(head.Timestamp * 1000, _genesisTimestampMs);
                _slotToBlockHash.Set(slot, head.Hash);

                if (_blockWaitTasks.Remove(slot, out TaskCompletionSource<Block?>? tcs))
                {
                    tcs?.TrySetResult(head);
                }
            }
        }
        else if (_logger.IsDebug)
        {
            _logger.Warn($"Shutter block handler not running, outdated block {head.Number}");
        }
    }

    public async Task<Block?> WaitForBlockInSlot(ulong slot, TimeSpan slotLength, TimeSpan cutoff, CancellationToken cancellationToken)
    {
        TaskCompletionSource<Block?>? tcs = null;
        lock (_syncObject)
        {
            if (_slotToBlockHash.TryGet(slot, out Hash256? blockHash))
            {
                return blockTree.FindBlock(blockHash!);
            }

            _logger.Debug($"Waiting for block in {slot} to get Shutter transactions.");

            long offset = shutterTime.GetCurrentOffsetMs(slot, _genesisTimestampMs);
            long waitTime = (long)cutoff.TotalMilliseconds - offset;
            if (waitTime <= 0)
            {
                _logger.Debug($"Shutter no longer waiting for block in slot {slot}, offset of {offset}ms is after cutoff of {(int)cutoff.TotalMilliseconds}ms.");
                return null;
            }
            waitTime = Math.Min(waitTime, 2 * (long)slotLength.TotalMilliseconds);

            var timeoutSource = new CancellationTokenSource((int)waitTime);
            var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
            CancellationTokenRegistration ctr = source.Token.Register(() => CancelWaitForBlock(slot));
            tcs = _blockWaitTasks.GetOrAdd(slot, _ => new());

            _cts.Add(timeoutSource);
            _cts.Add(source);
            _ctr.Add(ctr);
        }
        return await tcs.Task;
    }

    private void CancelWaitForBlock(ulong slot)
    {
        _blockWaitTasks.Remove(slot, out TaskCompletionSource<Block?>? cancelledWaitTask);
        cancelledWaitTask?.TrySetException(new OperationCanceledException());
    }

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

    public void Dispose()
    {
        foreach (CancellationTokenSource cts in _cts)
        {
            cts.Dispose();
        }

        foreach (CancellationTokenRegistration ctr in _ctr)
        {
            ctr.Dispose();
        }
    }
}
