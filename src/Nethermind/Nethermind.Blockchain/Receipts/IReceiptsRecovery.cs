// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Blockchain.Receipts
{
    public interface IReceiptsRecovery
    {
        ReceiptsRecoveryResult TryRecover(Block block, TxReceipt[] receipts, bool forceRecoverSender = true);
        bool NeedRecover(TxReceipt[] receipts, bool forceRecoverSender = true);
    }
}
