// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

        public event EventHandler<ReceiptsEventArgs>? ReceiptsInserted;

        public ReceiptCanonicalityMonitor(IReceiptStorage? receiptStorage, ILogManager? logManager)
        {
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _receiptStorage.ReceiptsInserted += OnBlockAddedToMain;
        }

        private void OnBlockAddedToMain(object sender, BlockReplacementEventArgs e)
        {
            // we don't want this to be on main processing thread
            Task.Run(() => TriggerReceiptInsertedEvent(e.Block, e.PreviousBlock));
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

        public void Dispose()
        {
            _receiptStorage.ReceiptsInserted -= OnBlockAddedToMain;
        }
    }
}
