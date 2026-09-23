// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
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
        private readonly Lock _lock = new();
        private Task _dispatch = Task.CompletedTask;
        private volatile bool _disposed;

        public event EventHandler<ReceiptsEventArgs>? ReceiptsInserted;

        public ReceiptCanonicalityMonitor(IReceiptStorage? receiptStorage, IBlockTree blockTree, ILogManager? logManager)
        {
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _blockTree = blockTree;
            _logger = logManager?.GetClassLogger<ReceiptCanonicalityMonitor>() ?? throw new ArgumentNullException(nameof(logManager));
            _receiptStorage.NewCanonicalReceipts += OnBlockAddedToMain;
            _blockTree.BlockRemovedFromMain += OnBlockRemovedFromMain;
        }

        private void OnBlockAddedToMain(object? sender, BlockReplacementEventArgs e) => Enqueue(() =>
        {
            if (e.PreviousBlock is not null)
            {
                Publish(e.PreviousBlock, removed: true);
            }

            Publish(e.Block, removed: false);
        });

        private void OnBlockRemovedFromMain(object? sender, BlockHeaderEventArgs e) => Enqueue(() =>
        {
            if (_disposed || ReceiptsInserted is null) return;

            Block? block = _blockTree.FindBlock(e.Header.Hash!, BlockTreeLookupOptions.TotalDifficultyNotNeeded, e.Header.Number);
            if (block is not null)
            {
                Publish(block, removed: true);
            }
            else if (_logger.IsDebug)
            {
                _logger.Debug($"Removed block {e.Header.ToString(BlockHeader.Format.FullHashAndNumber)} not found, its removed logs are not published.");
            }
        });

        private void Enqueue(Action action)
        {
            lock (_lock)
            {
                _dispatch = _dispatch.ContinueWith(_ => action(), TaskScheduler.Default);
            }
        }

        private void Publish(Block block, bool removed)
        {
            EventHandler<ReceiptsEventArgs>? handlers = ReceiptsInserted;
            if (_disposed || handlers is null) return;

            ReceiptsEventArgs e;
            try
            {
                e = new(block.Header, _receiptStorage.Get(block), removed);
            }
            catch (Exception exception)
            {
                if (_logger.IsError) _logger.Error($"Couldn't read receipts of {(removed ? "removed" : "new")} block {block.ToString(Block.Format.FullHashAndNumber)}.", exception);
                return;
            }

            foreach (EventHandler<ReceiptsEventArgs> handler in Delegate.EnumerateInvocationList(handlers))
            {
                try
                {
                    handler(this, e);
                }
                catch (Exception exception)
                {
                    if (_logger.IsError) _logger.Error($"Receipts subscriber {handler.Method.Name} failed for block {block.ToString(Block.Format.FullHashAndNumber)}.", exception);
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
