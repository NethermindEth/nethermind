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

    public class ReceiptCanonicalityMonitor : IReceiptMonitor
    {
        private readonly IReceiptStorage _receiptStorage;
        private readonly ILogger _logger;
        private readonly Lock _dispatchLock = new();
        private Task _dispatch = Task.CompletedTask;

        public event EventHandler<ReceiptsEventArgs>? ReceiptsInserted;

        public ReceiptCanonicalityMonitor(IReceiptStorage? receiptStorage, ILogManager? logManager)
        {
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _logger = logManager?.GetClassLogger<ReceiptCanonicalityMonitor>() ?? throw new ArgumentNullException(nameof(logManager));
            _receiptStorage.NewCanonicalReceipts += OnBlockAddedToMain;
        }

        private void OnBlockAddedToMain(object sender, BlockReplacementEventArgs e)
        {
            // Off the main processing thread, but chained so subscribers see blocks (and a reorg's removed receipts)
            // in canonicalisation order.
            lock (_dispatchLock)
            {
                _dispatch = _dispatch.ContinueWith(_ => TriggerReceiptInsertedEvent(e.Block, e.PreviousBlock), TaskScheduler.Default);
            }
        }

        private void TriggerReceiptInsertedEvent(Block newBlock, Block? previousBlock)
        {
            try
            {
                if (previousBlock is not null)
                {
                    TxReceipt[] removedReceipts = _receiptStorage.Get(previousBlock);
                    ReceiptsInserted?.Invoke(this, new ReceiptsEventArgs(previousBlock.Header, removedReceipts, true));
                }

                TxReceipt[] insertedReceipts = _receiptStorage.Get(newBlock);
                ReceiptsInserted?.Invoke(this, new ReceiptsEventArgs(newBlock.Header, insertedReceipts));
            }
            catch (Exception exception)
            {
                if (_logger.IsError) _logger.Error($"Couldn't correctly trigger receipt event. New block {newBlock.ToString(Block.Format.FullHashAndNumber)}, Prev block {previousBlock?.ToString(Block.Format.FullHashAndNumber)}.", exception);
            }
        }

        public void Dispose() => _receiptStorage.NewCanonicalReceipts -= OnBlockAddedToMain;
    }
}
