// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Blockchain
{
    public interface IReceiptMonitor : IDisposable
    {
        event EventHandler<ReceiptsEventArgs> ReceiptsInserted;
    }

    /// <remarks>
    /// Events are raised off the main processing thread, one at a time in canonicalisation order.
    /// </remarks>
    public class ReceiptCanonicalityMonitor : IReceiptMonitor
    {
        private readonly IReceiptStorage _receiptStorage;
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;
        private readonly Eip8116Schedule _eip8116;
        private readonly bool _captureReceipts;
        private readonly long _maxQueuedReceiptBytes;
        private long _queuedReceiptBytes;
        private bool _warnedQueueCapacity;
        private readonly Lock _lock = new();
        private Task _dispatch = Task.CompletedTask;
        private volatile bool _disposed;

        public event EventHandler<ReceiptsEventArgs>? ReceiptsInserted;

        public ReceiptCanonicalityMonitor(IReceiptStorage? receiptStorage, IBlockTree blockTree, ILogManager? logManager, ISpecProvider specProvider, IReceiptConfig? receiptConfig = null)
        {
            _eip8116 = new Eip8116Schedule(specProvider);
            _captureReceipts = receiptConfig is { StoreReceipts: false };
            _maxQueuedReceiptBytes = (long)(receiptConfig?.MaxTransientReceiptStorageSize ?? 128) * 1024 * 1024;
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _blockTree = blockTree;
            _logger = logManager?.GetClassLogger<ReceiptCanonicalityMonitor>() ?? throw new ArgumentNullException(nameof(logManager));
            _receiptStorage.NewCanonicalReceipts += OnBlockAddedToMain;
            _blockTree.BlockRemovedFromMain += OnBlockRemovedFromMain;
        }

        private void OnBlockAddedToMain(object? sender, BlockReplacementEventArgs e)
        {
            if (_captureReceipts)
            {
                // Transient receipts can disappear at the next finalization or capacity eviction. Capture their
                // arrays now, while subscriber delivery remains asynchronous and ordered as before.
                ReceiptsEventArgs? removed = e.PreviousBlock is { } previous ? Read(previous.Header, true, previous) : null;
                ReceiptsEventArgs? added = Read(e.Block.Header, false, e.Block);
                EnqueueCaptured(removed, added);
            }
            else
            {
                Enqueue(() =>
                {
                    if (e.PreviousBlock is { } previous) Publish(previous, removed: true);
                    Publish(e.Block, removed: false);
                });
            }
        }

        private void OnBlockRemovedFromMain(object? sender, BlockHeaderEventArgs e)
        {
            if (_captureReceipts)
            {
                ReceiptsEventArgs? removed = Read(e.Header, true);
                EnqueueCaptured(removed, null);
            }
            else
            {
                Enqueue(() =>
                {
                    if (_disposed || ReceiptsInserted is null) return;
                    Block? block = _blockTree.FindBlock(e.Header.Hash!, BlockTreeLookupOptions.TotalDifficultyNotNeeded, e.Header.Number);
                    if (block is not null)
                        Publish(block, removed: true);
                    else if (_logger.IsDebug)
                        _logger.Debug($"Removed block {e.Header.ToString(BlockHeader.Format.FullHashAndNumber)} not found, its removed logs are not published.");
                });
            }
        }

        private void EnqueueCaptured(ReceiptsEventArgs? removed, ReceiptsEventArgs? added)
        {
            if (removed is null && added is null) return;
            long size = (removed is null ? 0 : TransientReceiptStorage.EstimateSize(removed.TxReceipts))
                + (added is null ? 0 : TransientReceiptStorage.EstimateSize(added.TxReceipts));
            lock (_lock)
            {
                // Captured arrays outlive storage eviction. Bound delivery separately so a slow subscriber cannot
                // turn a bounded transient store into an unbounded queue of receipts.
                if (size > _maxQueuedReceiptBytes - _queuedReceiptBytes)
                {
                    if (!_warnedQueueCapacity && _logger.IsWarn)
                        _logger.Warn($"Transient receipt notification queue reached its {_maxQueuedReceiptBytes} byte budget. Notifications outside the delivery budget are skipped.");
                    _warnedQueueCapacity = true;
                    return;
                }
                _queuedReceiptBytes += size;
                _dispatch = _dispatch.ContinueWith(_ =>
                {
                    try
                    {
                        if (removed is not null) Publish(removed);
                        if (added is not null) Publish(added);
                    }
                    finally
                    {
                        lock (_lock) _queuedReceiptBytes -= size;
                    }
                }, TaskScheduler.Default);
            }
        }

        private void Enqueue(Action action)
        {
            lock (_lock)
            {
                _dispatch = _dispatch.ContinueWith(_ => action(), TaskScheduler.Default);
            }
        }

        private void Publish(Block block, bool removed)
        {
            ReceiptsEventArgs? e = Read(block.Header, removed, block);
            if (e is not null) Publish(e);
        }

        private ReceiptsEventArgs? Read(BlockHeader header, bool removed, Block? block = null)
        {
            if (_disposed || ReceiptsInserted is null) return null;
            try
            {
                TxReceipt[] receipts = block is null ? _receiptStorage.Get(header.Hash!) : _receiptStorage.Get(block);
                return new(header, receipts, removed, _eip8116.IsEnabled(header.Number, header.Timestamp));
            }
            catch (Exception exception)
            {
                if (_logger.IsError) _logger.Error($"Couldn't read receipts of {(removed ? "removed" : "new")} block {header.ToString(BlockHeader.Format.FullHashAndNumber)}.", exception);
                return null;
            }
        }

        private void Publish(ReceiptsEventArgs e)
        {
            EventHandler<ReceiptsEventArgs>? handlers = ReceiptsInserted;
            if (_disposed || handlers is null) return;

            foreach (EventHandler<ReceiptsEventArgs> handler in Delegate.EnumerateInvocationList(handlers))
            {
                try
                {
                    handler(this, e);
                }
                catch (Exception exception)
                {
                    if (_logger.IsError) _logger.Error($"Receipts subscriber {handler.Method.Name} failed for block {e.BlockHeader.ToString(BlockHeader.Format.FullHashAndNumber)}.", exception);
                }
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _receiptStorage.NewCanonicalReceipts -= OnBlockAddedToMain;
            _blockTree.BlockRemovedFromMain -= OnBlockRemovedFromMain;
        }
    }
}
