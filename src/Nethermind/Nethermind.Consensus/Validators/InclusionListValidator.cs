// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.TxPool.Comparison;

namespace Nethermind.Consensus.Validators;

/// <summary>
/// EIP-7805 (FOCIL) IL satisfaction check. A block satisfies the IL iff every IL tx is
/// either already included in <see cref="Block.Transactions"/>, or no longer "appendable"
/// against the post-IL state — i.e., applying it as the next tx after only the IL txs
/// the block already includes would fail (nonce gap, insufficient balance, base-fee
/// shortfall, or not enough remaining gas).
/// </summary>
/// <remarks>
/// The reference state for the appendability check is the PARENT block's state, with the
/// effects of any IL transactions the block already includes applied on top. Validating
/// against post-execution state would let a censoring builder defeat the IL by including a
/// same-nonce replacement tx from the IL sender: the replacement bumps the sender's nonce
/// in the post-state, making the original IL tx look "not appendable" and the IL look
/// satisfied. Validating against parent state closes that gap.
/// </remarks>
public class InclusionListValidator(ISpecProvider specProvider) : IInclusionListValidator
{
    public bool ValidateInclusionList(Block block, IReadOnlyDictionary<AddressAsKey, AccountSnapshot> parentSenderState) =>
        ValidateInclusionList(block, parentSenderState, specProvider.GetSpec(block.Header));

    private static bool ValidateInclusionList(
        Block block,
        IReadOnlyDictionary<AddressAsKey, AccountSnapshot> parentSenderState,
        IReleaseSpec spec)
    {
        if (!spec.InclusionListsEnabled)
        {
            return true;
        }

        if (block.InclusionListTransactions is null)
        {
            return false;
        }

        // FOCIL is a conditional IL: once the block has no gas left for even a base-cost
        // transfer, by definition nothing more is appendable, so the IL is trivially satisfied.
        if (block.GasUsed + Transaction.BaseTxGasCost > block.GasLimit)
        {
            return true;
        }

        // Build the included-tx set once: O(n) construction trades a hash for the O(n*m)
        // worst case of scanning block.Transactions per IL tx. ByHashTxComparer matches what
        // the rest of the codebase uses for tx-set lookups so cross-instance hash collisions
        // can't false-match.
        HashSet<Transaction> includedTxs = new(block.Transactions, ByHashTxComparer.Instance);

        // For each IL tx that's actually in the block, accumulate its effect on the sender's
        // parent-state nonce and balance — per EIP-7805 the reference state for the
        // appendability check of remaining IL txs is parent state PLUS the IL txs the block
        // includes. The cost we subtract is the worst-case (gasLimit * maxFeePerGas + value);
        // it matches TransactionProcessor.cs's pre-execution balance check so we can't accept
        // a block whose IL tx would actually fail at execution time.
        Dictionary<AddressAsKey, IlDelta> ilDeltas = [];
        foreach (Transaction tx in block.InclusionListTransactions)
        {
            if (tx.SenderAddress is null || !includedTxs.Contains(tx))
            {
                continue;
            }

            UInt256 cost = tx.Value + (UInt256)tx.GasLimit * tx.MaxFeePerGas;
            ilDeltas.TryGetValue(tx.SenderAddress, out IlDelta d);
            ilDeltas[tx.SenderAddress] = new IlDelta(d.IncludedCount + 1, d.Cost + cost);
        }

        foreach (Transaction tx in block.InclusionListTransactions)
        {
            if (!includedTxs.Contains(tx) && CouldIncludeTx(tx, block, parentSenderState, ilDeltas))
            {
                return false;
            }
        }

        return true;
    }

    private static bool CouldIncludeTx(
        Transaction tx,
        Block block,
        IReadOnlyDictionary<AddressAsKey, AccountSnapshot> parentSenderState,
        Dictionary<AddressAsKey, IlDelta> ilDeltas)
    {
        // A tx whose signature didn't recover has no sender we can score against state.
        // Treat it as not-appendable so we don't NRE on a null lookup and don't falsely
        // report the IL as unsatisfied for an unsignable entry.
        if (tx.SenderAddress is null)
        {
            return false;
        }

        if (block.GasUsed + tx.GasLimit > block.GasLimit)
        {
            return false;
        }

        // EIP-1559: the fee cap (MaxFeePerGas) is what's required to be ≥ baseFee, not the
        // priority-tip (which is what GasPrice returns for type-2 txs). Using GasPrice would
        // wrongly reject perfectly valid IL txs whose tip is below the baseFee but whose cap
        // is above, letting a builder skip them. Same field is used for the balance bound,
        // matching TransactionProcessor's worst-case calculation.
        if (tx.MaxFeePerGas < block.BaseFeePerGas)
        {
            return false;
        }

        AccountSnapshot baseAcc = parentSenderState.GetValueOrDefault(tx.SenderAddress, AccountSnapshot.Empty);
        ilDeltas.TryGetValue(tx.SenderAddress, out IlDelta delta);

        if (tx.Nonce != baseAcc.Nonce + delta.IncludedCount)
        {
            return false;
        }

        UInt256 remainingBalance = baseAcc.Balance >= delta.Cost ? baseAcc.Balance - delta.Cost : UInt256.Zero;
        UInt256 txCost = tx.Value + (UInt256)tx.GasLimit * tx.MaxFeePerGas;
        return remainingBalance >= txCost;
    }

    private readonly record struct IlDelta(ulong IncludedCount, UInt256 Cost);
}

/// <summary>
/// Snapshot of an account's nonce and balance at a specific block — captured before block
/// processing so EIP-7805's IL satisfaction check can run against the parent block's state
/// rather than the live post-execution worldstate.
/// </summary>
public readonly record struct AccountSnapshot(UInt256 Balance, UInt256 Nonce)
{
    public static readonly AccountSnapshot Empty = default;
}
