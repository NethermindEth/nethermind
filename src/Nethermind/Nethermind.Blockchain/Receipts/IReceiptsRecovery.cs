// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Receipts
{
    public interface IReceiptsRecovery
    {
        ReceiptsRecoveryResult TryRecover(ReceiptRecoveryBlock block, TxReceipt[] receipts, bool forceRecoverSender = true);

        ReceiptsRecoveryResult TryRecover(Block block, TxReceipt[] receipts, bool forceRecoverSender = true) =>
            TryRecover(new ReceiptRecoveryBlock(block), receipts, forceRecoverSender);

        bool NeedRecover(TxReceipt[] receipts, bool forceRecoverSender = true, bool recoverSenderOnly = false);

        IRecoveryContext CreateRecoveryContext(ReceiptRecoveryBlock block, bool forceRecoverSender = false);

        public interface IRecoveryContext : IDisposable
        {
            void RecoverReceiptData(TxReceipt receipt);
            void RecoverReceiptData(ref TxReceiptStructRef receipt);
        }
    }
}
