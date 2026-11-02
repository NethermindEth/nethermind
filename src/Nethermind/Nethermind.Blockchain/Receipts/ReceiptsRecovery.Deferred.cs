// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Receipts;

public partial class ReceiptsRecovery
{
    internal IReceiptsRecovery.IDeferredLogRecoveryContext CreateDeferredLogRecoveryContext<TBlockSource>(TBlockSource source)
        where TBlockSource : struct, IReceiptsRecovery.IRecoveryBlockSource => new DeferredLogRecoveryContext<TBlockSource>(this, source);

    private sealed class DeferredLogRecoveryContext<TBlockSource>(ReceiptsRecovery recovery, TBlockSource source)
        : IReceiptsRecovery.IDeferredLogRecoveryContext
        where TBlockSource : struct, IReceiptsRecovery.IRecoveryBlockSource
    {
        private LogRecoveryContext? _context;

        public Hash256 RecoverReceiptData(ref TxReceiptStructRef receipt, int transactionIndex) =>
            GetContext().RecoverReceiptData(ref receipt, transactionIndex);

        public void RecoverReceiptData(ref TxReceiptStructRef receipt) => GetContext().RecoverReceiptData(ref receipt);

        public void RecoverReceiptData(TxReceipt receipt) => GetContext().RecoverReceiptData(receipt);

        public IReceiptsRecovery.IRecoveryContext CreateFullRecoveryContext() => recovery.CreateRecoveryContext(source.GetBlock());

        private LogRecoveryContext GetContext() => _context ??= new LogRecoveryContext(source.GetBlock());

        public void Dispose() => _context?.Dispose();
    }
}
