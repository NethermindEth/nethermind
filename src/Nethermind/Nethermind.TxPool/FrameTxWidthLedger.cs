// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.TxPool;

/// <summary>
/// Holds the MATCHA width each sender has earned from finalized keyed-nonce frame transactions, and the width
/// each paymaster has earned from the finalized frame transactions it paid for.
/// </summary>
/// <remarks>
/// One instance is shared by the transaction pool, which spends width on admission, and the finalization source,
/// which earns it. Credits are capped per sender and per paymaster at <see cref="ITxPoolConfig.FrameTxWidthCap"/>. Inert unless
/// <see cref="ITxPoolConfig.FrameTxWidthEnabled"/> is set.
/// </remarks>
public sealed class FrameTxWidthLedger(ITxPoolConfig txPoolConfig, ILogManager logManager) : IFrameTxWidthLedger
{
    private readonly ILogger _logger = logManager.GetClassLogger<FrameTxWidthLedger>();
    private int _missingReceiptsWarned;

    internal SenderWidthCache SenderWidth { get; } = new();

    internal SenderWidthCache PaymasterWidth { get; } = new(holdsPaymasters: true);

    /// <inheritdoc/>
    public void EarnWidthOnFinalization(Block finalizedBlock, TxReceipt[] receipts)
    {
        if (!txPoolConfig.FrameTxWidthEnabled) return;

        Transaction[] blockTransactions = finalizedBlock.Transactions;
        if (receipts.Length != blockTransactions.Length)
        {
            if (Interlocked.Exchange(ref _missingReceiptsWarned, 1) == 0)
            {
                if (_logger.IsWarn) _logger.Warn($"Skipped MATCHA width for finalized block {finalizedBlock.Number}: {receipts.Length} receipts for {blockTransactions.Length} transactions. Further skips are logged at debug level.");
            }
            else if (_logger.IsDebug)
            {
                _logger.Debug($"Skipped MATCHA width for finalized block {finalizedBlock.Number}: {receipts.Length} receipts for {blockTransactions.Length} transactions");
            }

            return;
        }

        for (int i = 0; i < blockTransactions.Length; i++)
        {
            Transaction blockTx = blockTransactions[i];
            if (blockTx.SupportsFrames && KeyedNonceManager.UsesKeyedNonce(blockTx))
            {
                SenderWidth.Earn(blockTx.SenderAddress!, (UInt256)receipts[i].GasUsed, txPoolConfig.FrameTxWidthCap);
            }

            if (blockTx.SupportsFrames && receipts[i].Payer is Address paymaster && paymaster != blockTx.SenderAddress)
            {
                PaymasterWidth.Earn(paymaster, (UInt256)receipts[i].GasUsed, txPoolConfig.FrameTxWidthCap);
            }
        }
    }
}
