// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.TxPool;

/// <summary>
/// Earns MATCHA sender width from the EIP-8250 keyed-nonce frame transactions in a finalized block.
/// </summary>
/// <remarks>
/// Implemented by the pool that holds the width ledger and driven by the consensus layer that observes
/// finalization, so a sender earns width only from gas that can no longer reorg out and the pool keeps no
/// reference to the block tree that raises the signal.
/// </remarks>
public interface IFrameTxWidthLedger
{
    void EarnWidthOnFinalization(Block finalizedBlock, TxReceipt[] receipts);
}
