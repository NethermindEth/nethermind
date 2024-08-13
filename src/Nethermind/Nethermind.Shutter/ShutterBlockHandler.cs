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
using Nethermind.Core.Specs;

namespace Nethermind.Shutter;

public class ShutterBlockHandler(
    ulong chainId,
    string validatorRegistryContractAddress,
    ulong validatorRegistryMessageVersion,
    ReadOnlyTxProcessingEnvFactory envFactory,
    IAbiEncoder abiEncoder,
    IReceiptFinder receiptFinder,
    ISpecProvider specProvider,
    Dictionary<ulong, byte[]> validatorsInfo,
    ShutterEon eon,
    ShutterTxLoader txLoader,
    ILogManager logManager) : IShutterBlockHandler
{
    private readonly ILogger _logger = logManager.GetClassLogger();
    private readonly TimeSpan _upToDateCutoff = TimeSpan.FromSeconds(5);
    private bool _haveCheckedRegistered = false;
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<Block?>> _blockWaitTasks = new();
    private readonly LruCache<ulong, Block?> _blockCache = new(5, "Block cache");
    private readonly object _syncObject = new();
    private readonly ulong _genesisTimestampMs = ShutterHelpers.GetGenesisTimestampMs(specProvider);

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

            lock (_syncObject)
            {
                ulong slot = ShutterHelpers.GetSlot(head.Timestamp * 1000, _genesisTimestampMs);
                _blockCache.Set(slot, head);

                if (_blockWaitTasks.Remove(slot, out TaskCompletionSource<Block?>? tcs))
                {
                    tcs?.TrySetResult(head);
                }
            }
        }
        else
        {
            // todo: made debug
            _logger.Warn($"Shutter block handler not running, outdated block {head.Number}");
        }
    }

    public async Task<Block?> WaitForBlockInSlot(ulong slot, TimeSpan slotLength, TimeSpan cutoff, CancellationToken cancellationToken)
    {
        TaskCompletionSource<Block?>? tcs = null;
        lock (_syncObject)
        {
            if (_blockCache.TryGet(slot, out Block? block))
            {
                return block;
            }

            // todo: make debug
            _logger.Info($"Waiting for block in {slot} (Shutter).");

            long offset = ShutterHelpers.GetCurrentOffsetMs(slot, _genesisTimestampMs);
            long waitTime = (long)cutoff.TotalMilliseconds - offset;
            if (waitTime <= 0)
            {
                _logger.Warn($"Block missed in slot {slot}, offset of {offset}ms is after cutoff of {(int)cutoff.TotalMilliseconds}ms (Shutter).");
                return null;
            }
            waitTime = Math.Min(waitTime, 2 * (long)slotLength.TotalMilliseconds);

            using var timeoutSource = new CancellationTokenSource((int)waitTime);
            using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

            using (source.Token.Register(() => CancelWaitForBlock(slot)))
            {
                tcs = _blockWaitTasks.GetOrAdd(slot, _ => new());
            }
        }
        return await tcs.Task;
    }

    private void CancelWaitForBlock(ulong slot)
    {
        _blockWaitTasks.Remove(slot, out TaskCompletionSource<Block?>? cancelledWaitTask);
        cancelledWaitTask?.TrySetException(new OperationCanceledException());
    }

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
