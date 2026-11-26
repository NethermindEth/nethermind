// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.TxPool;

/// <summary>
/// Earns MATCHA sender width from the EIP-8250 keyed-nonce frame transactions in a finalized block, and
/// paymaster width from the frame transactions a paymaster paid for.
/// </summary>
public interface IFrameTxWidthLedger
{
    /// <summary>Credits each sender of a keyed-nonce frame transaction in <paramref name="finalizedBlock"/>, and the paymaster of each sponsored one, with the gas it used.</summary>
    /// <param name="finalizedBlock">A canonical finalized block. Each block must be passed at most once.</param>
    /// <param name="receipts">The block's receipts in transaction order, one per transaction, carrying per-transaction gas used.
    /// A count mismatch skips the block.</param>
    void EarnWidthOnFinalization(Block finalizedBlock, TxReceipt[] receipts);
}
