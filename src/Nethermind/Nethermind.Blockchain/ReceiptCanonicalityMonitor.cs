// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
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
    /// Each subscriber gets the events in canonicalisation order on its own queue, so a slow handler delays only itself.
    /// </remarks>
    public class ReceiptCanonicalityMonitor : IReceiptMonitor
    {
        private readonly IReceiptStorage _receiptStorage;
        private readonly ILogger _logger;
        private readonly Lock _lock = new();
        private Task _dispatch = Task.CompletedTask;
        private volatile Subscriber[] _subscribers = [];

        public event EventHandler<ReceiptsEventArgs>? ReceiptsInserted
        {
            add
            {
                if (value is null) return;
                lock (_lock) _subscribers = [.. _subscribers, new Subscriber(value, _logger)];
            }
            remove
            {
                lock (_lock)
                {
                    int index = Array.FindLastIndex(_subscribers, s => s.Handler == value);
                    if (index < 0) return;
                    _subscribers[index].Removed = true;
                    _subscribers = [.. _subscribers.AsSpan(0, index), .. _subscribers.AsSpan(index + 1)];
                }
            }
        }

        public ReceiptCanonicalityMonitor(IReceiptStorage? receiptStorage, ILogManager? logManager)
        {
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _logger = logManager?.GetClassLogger<ReceiptCanonicalityMonitor>() ?? throw new ArgumentNullException(nameof(logManager));
            _receiptStorage.NewCanonicalReceipts += OnBlockAddedToMain;
        }

        private void OnBlockAddedToMain(object sender, BlockReplacementEventArgs e)
        {
            // Off the main processing thread, but chained so the receipts are read and handed out in canonicalisation order.
            lock (_lock)
            {
                _dispatch = _dispatch.ContinueWith(_ => TriggerReceiptInsertedEvent(e.Block, e.PreviousBlock), TaskScheduler.Default);
            }
        }

        private void TriggerReceiptInsertedEvent(Block newBlock, Block? previousBlock)
        {
            if (previousBlock is not null)
            {
                Publish(previousBlock, removed: true);
            }

            Publish(newBlock, removed: false);
        }

        private void Publish(Block block, bool removed)
        {
            // Also skips the receipts read once the monitor is disposed.
            Subscriber[] subscribers = _subscribers;
            if (subscribers.Length == 0) return;

            try
            {
                ReceiptsEventArgs e = new(block.Header, _receiptStorage.Get(block), removed);
                foreach (Subscriber subscriber in subscribers)
                {
                    subscriber.Enqueue(this, e);
                }
            }
            catch (Exception exception)
            {
                if (_logger.IsError) _logger.Error($"Couldn't correctly trigger receipt event for {(removed ? "removed" : "new")} block {block.ToString(Block.Format.FullHashAndNumber)}.", exception);
            }
        }

        public void Dispose()
        {
            _receiptStorage.NewCanonicalReceipts -= OnBlockAddedToMain;
            lock (_lock)
            {
                foreach (Subscriber subscriber in _subscribers)
                {
                    subscriber.Removed = true;
                }

                _subscribers = [];
            }
        }

        private sealed class Subscriber(EventHandler<ReceiptsEventArgs> handler, ILogger logger)
        {
            private Task _tail = Task.CompletedTask;

            public EventHandler<ReceiptsEventArgs> Handler => handler;

            /// <summary>Set on unsubscribe, so events still queued for this subscriber are dropped.</summary>
            public volatile bool Removed;

            /// <remarks>Only called from the monitor's dispatch chain, so appends never race.</remarks>
            public void Enqueue(object sender, ReceiptsEventArgs e) =>
                _tail = _tail.ContinueWith(_ => Invoke(sender, e), TaskScheduler.Default);

            private void Invoke(object sender, ReceiptsEventArgs e)
            {
                if (Removed) return;

                try
                {
                    handler(sender, e);
                }
                catch (Exception exception)
                {
                    if (logger.IsError) logger.Error($"Receipts subscriber {handler.Method.Name} failed for block {e.BlockHeader.ToString(BlockHeader.Format.FullHashAndNumber)}.", exception);
                }
            }
        }
    }
}
