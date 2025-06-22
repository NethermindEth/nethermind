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
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Blockchain;
using Nethermind.Core.Collections;
using Nethermind.Shutter.Config;

namespace Nethermind.Shutter;

public class ShutterBlockHandler : IShutterBlockHandler
{
    private readonly ILogger _logger;
    private readonly SlotTime _time;
    private readonly IShutterEon _eon;
    private readonly IReceiptFinder _receiptFinder;
    private readonly ShutterTxLoader _txLoader;
    private readonly ShutterValidatorsInfo _validatorsInfo;
    private readonly ILogManager _logManager;
    private readonly IAbiEncoder _abiEncoder;
    private readonly IBlockTree _blockTree;
    private readonly ReadOnlyBlockTree _readOnlyBlockTree;
    private readonly ulong _chainId;
    private readonly IShutterConfig _cfg;
    private readonly TimeSpan _slotLength;
    private readonly TimeSpan _blockWaitCutoff;
    private readonly IReadOnlyTxProcessingEnvFactory _envFactory;
    private bool _haveCheckedRegistered = false;
    private ulong _blockWaitTaskId = 0;
    private readonly Dictionary<ulong, Dictionary<ulong, BlockWaitTask>> _blockWaitTasks = [];
    private readonly LruCache<ulong, Hash256?> _slotToBlockHash = new(5, "Slot to block hash mapping");
    private readonly Lock _syncObject = new();

    public ShutterBlockHandler(
        ulong chainId,
        IShutterConfig cfg,
        IReadOnlyTxProcessingEnvFactory envFactory,
        IBlockTree blockTree,
        IAbiEncoder abiEncoder,
        IReceiptFinder receiptFinder,
        ShutterValidatorsInfo validatorsInfo,
        IShutterEon eon,
        ShutterTxLoader txLoader,
        SlotTime time,
        ILogManager logManager,
        TimeSpan slotLength,
        TimeSpan blockWaitCutoff)
    {
        _chainId = chainId;
        _cfg = cfg;
        _logger = logManager.GetClassLogger();
        _time = time;
        _validatorsInfo = validatorsInfo;
        _eon = eon;
        _receiptFinder = receiptFinder;
        _txLoader = txLoader;
        _blockTree = blockTree;
        _readOnlyBlockTree = blockTree.AsReadOnly();
        _abiEncoder = abiEncoder;
        _logManager = logManager;
        _envFactory = envFactory;
        _slotLength = slotLength;
        _blockWaitCutoff = blockWaitCutoff;

        _blockTree.NewHeadBlock += OnNewHeadBlock;
    }

    public async Task<Block?> WaitForBlockInSlot(ulong slot, CancellationToken cancellationToken, Func<int, CancellationTokenSource>? initTimeoutSource = null)
    {
        TaskCompletionSource<Block?>? tcs = null;
        lock (_syncObject)
        {
            if (_slotToBlockHash.TryGet(slot, out Hash256? blockHash))
            {
                return _readOnlyBlockTree.FindBlock(blockHash!, BlockTreeLookupOptions.None);
            }

            if (_logger.IsDebug) _logger.Debug($"Waiting for block in {slot} to get Shutter transactions.");

            tcs = new();

            long offset = _time.GetCurrentOffsetMs(slot);
            long waitTime = (long)_blockWaitCutoff.TotalMilliseconds - offset;
            if (waitTime <= 0)
            {
                if (_logger.IsDebug) _logger.Debug($"Shutter no longer waiting for block in slot {slot}, offset of {offset}ms is after cutoff of {(int)_blockWaitCutoff.TotalMilliseconds}ms.");
                return null;
            }
            waitTime = Math.Min(waitTime, 2 * (long)_slotLength.TotalMilliseconds);

            ulong taskId = _blockWaitTaskId++;
            CancellationTokenSource timeoutSource = initTimeoutSource is null ? new CancellationTokenSource((int)waitTime) : initTimeoutSource((int)waitTime);
            CancellationTokenRegistration ctr = cancellationToken.Register(() => CancelWaitForBlock(slot, taskId, false));
            CancellationTokenRegistration timeoutCtr = timeoutSource.Token.Register(() => CancelWaitForBlock(slot, taskId, true));

            if (!_blockWaitTasks.ContainsKey(slot))
            {
                _blockWaitTasks.Add(slot, []);
            }

            Dictionary<ulong, BlockWaitTask> slotWaitTasks = _blockWaitTasks.GetValueOrDefault(slot)!;
            slotWaitTasks.Add(taskId, new BlockWaitTask()
            {
                Tcs = tcs,
                TimeoutSource = timeoutSource,
                CancellationRegistration = ctr,
                TimeoutCancellationRegistration = timeoutCtr
            });
        }
        return await tcs.Task;
    }

    public void Dispose()
    {
        _blockTree.NewHeadBlock -= OnNewHeadBlock;
        _blockWaitTasks.ForEach(static x => x.Value.ForEach(static waitTask =>
        {
            waitTask.Value.CancellationRegistration.Dispose();
            waitTask.Value.TimeoutCancellationRegistration.Dispose();
        }));
    }

    private void CancelWaitForBlock(ulong slot, ulong taskId, bool timeout)
    {
        if (_blockWaitTasks.TryGetValue(slot, out Dictionary<ulong, BlockWaitTask>? slotWaitTasks))
        {
            if (slotWaitTasks.TryGetValue(taskId, out BlockWaitTask waitTask))
            {
                if (timeout)
                {
                    waitTask.Tcs.TrySetResult(null);
                }
                else
                {
                    waitTask.Tcs.SetException(new OperationCanceledException());
                }
                waitTask.CancellationRegistration.Dispose();
                waitTask.TimeoutCancellationRegistration.Dispose();
            }
            slotWaitTasks.Remove(taskId);
        }
    }

    private void OnNewHeadBlock(object? _, BlockEventArgs e)
    {
        Block head = e.Block;
        if (_time.IsBlockUpToDate(head))
        {
            if (_logger.IsDebug) _logger.Debug($"Shutter block handler {head.Number}");

            if (!_haveCheckedRegistered)
            {
                CheckAllValidatorsRegistered(head.Header, _validatorsInfo);
                _haveCheckedRegistered = true;
            }
            _eon.Update(head.Header);
            _txLoader.LoadFromReceipts(head, _receiptFinder.Get(head), _eon.GetCurrentEonInfo()!.Value.Eon);

            lock (_syncObject)
            {
                ulong slot = _time.GetSlot(head.Timestamp * 1000);
                _slotToBlockHash.Set(slot, head.Hash);

                if (_blockWaitTasks.Remove(slot, out Dictionary<ulong, BlockWaitTask>? waitTasks))
                {
                    waitTasks.ForEach(waitTask =>
                    {
                        waitTask.Value.Tcs.TrySetResult(head);
                        waitTask.Value.Dispose();
                    });
                }
            }
        }
        else if (_logger.IsDebug)
        {
            _logger.Warn($"Shutter block handler not running, outdated block {head.Number}");
        }
    }


    private void CheckAllValidatorsRegistered(in BlockHeader parent, in ShutterValidatorsInfo validatorsInfo)
    {
        if (validatorsInfo.IsEmpty)
        {
            return;
        }

        IReadOnlyTxProcessingScope scope = _envFactory.Create().Build(parent.StateRoot!);
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

    private readonly struct BlockWaitTask : IDisposable
    {
        public TaskCompletionSource<Block?> Tcs { get; init; }
        public CancellationTokenSource TimeoutSource { get; init; }
        public CancellationTokenRegistration CancellationRegistration { get; init; }
        public CancellationTokenRegistration TimeoutCancellationRegistration { get; init; }

        public void Dispose()
        {
            TimeoutSource.Dispose();
            CancellationRegistration.Dispose();
            TimeoutCancellationRegistration.Dispose();
        }
    }
}
