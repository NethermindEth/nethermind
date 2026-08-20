// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Validators;

public static class InclusionListValidator
{
    public static bool IsSatisfied(Block block, IReadOnlyStateProvider state, IReleaseSpec spec, ITxValidator txValidator)
        => IsSatisfied(block, block.InclusionListTransactions, state, spec, txValidator);

    public static bool IsSatisfied(Block block, Transaction[]? il, IReadOnlyStateProvider state, IReleaseSpec spec, ITxValidator txValidator)
    {
        if (!spec.InclusionListsEnabled) return true;
        // No IL attached = non-engine-API path (genesis, RLP import); IL doesn't apply.
        if (il is null) return true;

        // No gas left for even the cheapest possible tx → nothing is appendable.
        // EIP-2780 lowers the base cost to 12000 (data-free self-transfer); pre-2780 it is 21000.
        ulong minIntrinsicGas = spec.IsEip2780Enabled ? GasCostOf.TransactionEip2780 : GasCostOf.Transaction;
        if (block.GasUsed + minIntrinsicGas > block.GasLimit) return true;

        // A flattened aggregate spans the whole committee, so exceeding the stackalloc bound is
        // reachable with conforming input rather than merely defensive — rent instead of allocating.
        bool[]? rented = il.Length > Eip7805Constants.MaxTransactionsPerInclusionList
            ? ArrayPool<bool>.Shared.Rent(il.Length)
            : null;
        try
        {
            Span<bool> included = rented is null ? stackalloc bool[il.Length] : rented.AsSpan(0, il.Length);
            included.Clear();
            return IsSatisfied(block, il, included, state, spec, txValidator);
        }
        finally
        {
            if (rented is not null) ArrayPool<bool>.Shared.Return(rented);
        }
    }

    private static bool IsSatisfied(Block block, Transaction[] il, Span<bool> included, IReadOnlyStateProvider state, IReleaseSpec spec, ITxValidator txValidator)
    {
        // Duplicate IL entries stay unmarked but fail the appendability check (nonce advanced).
        Dictionary<Hash256, int> ilByHash = new(il.Length);
        for (int i = 0; i < il.Length; i++)
        {
            Hash256? h = il[i].Hash;
            if (h is not null) ilByHash.TryAdd(h, i);
        }

        foreach (Transaction blockTx in block.Transactions)
        {
            if (blockTx.Hash is not null && ilByHash.TryGetValue(blockTx.Hash, out int idx))
                included[idx] = true;
        }

        Dictionary<AddressAsKey, AccountStruct>? senderCache = null;
        for (int i = 0; i < il.Length; i++)
        {
            if (!included[i] && CouldIncludeTx(il[i], block, state, spec, txValidator, ref senderCache)) return false;
        }
        return true;
    }

    private static bool CouldIncludeTx(Transaction tx, Block block, IReadOnlyStateProvider state, IReleaseSpec spec, ITxValidator txValidator, ref Dictionary<AddressAsKey, AccountStruct>? senderCache)
    {
        if (tx.SenderAddress is null) return false;
        // Doesn't fit in the block's remaining gas → can't be included. Subtract on the block side
        // (block.GasUsed <= block.GasLimit is invariant and the block-full case returned above) so this
        // ulong arithmetic can't underflow, unlike block.GasLimit - tx.GasLimit for an oversized tx.
        if (tx.GasLimit > block.GasLimit - block.GasUsed) return false;
        // Appendability must match normal execution: reuse the block validator's well-formedness
        // check (intrinsic gas, typed-tx rules, e.g. maxPriorityFeePerGas <= maxFeePerGas) instead of a subset.
        if (!txValidator.IsWellFormed(tx, spec, block.GasLimit)) return false;
        if (tx.MaxFeePerGas < block.BaseFeePerGas) return false;

        senderCache ??= [];
        ref AccountStruct account = ref CollectionsMarshal.GetValueRefOrAddDefault(senderCache, tx.SenderAddress, out bool cached);
        // Cache the negative result too (default struct = balance 0, nonce 0, empty codehash).
        if (!cached) state.TryGetAccount(tx.SenderAddress, out account);

        // EIP-3607: a sender with non-delegated code cannot send a tx.
        if (account.HasCode && !state.IsDelegatedCode(tx.SenderAddress)) return false;

        // Mirror TransactionProcessor.BuyGas: overflow-checked so an adversarial MaxFeePerGas can't wrap the cost below the balance.
        if (UInt256.MultiplyOverflow((UInt256)tx.GasLimit, tx.MaxFeePerGas, out UInt256 txCost)
            || UInt256.AddOverflow(txCost, tx.Value, out txCost))
            return false;

        // A blob tx must also cover maxFeePerBlobGas × blob gas up front, or it could never have executed.
        if (tx.SupportsBlobs
            && (!BlobGasCalculator.TryCalculateBlobMaxFee(tx.BlobVersionedHashes?.Length ?? 0, tx.MaxFeePerBlobGas ?? UInt256.Zero, out UInt256 blobFee)
                || UInt256.AddOverflow(txCost, blobFee, out txCost)))
            return false;

        return SpendableBalance(block, tx.SenderAddress, in account) >= txCost && account.Nonce == tx.Nonce;
    }

    /// <summary>
    /// Balance the sender would have had when an appended transaction executed.
    /// </summary>
    /// <remarks>
    /// Satisfaction is judged against post-block state, but withdrawals are credited after the
    /// block's transactions, so an appended transaction could never have spent them. Post-merge
    /// they are the only balance credit applied after execution, so removing them reconstructs
    /// the balance as of the end of the transaction phase.
    /// </remarks>
    private static UInt256 SpendableBalance(Block block, Address sender, ref readonly AccountStruct account)
    {
        UInt256 balance = account.Balance;
        if (block.Withdrawals is not { Length: > 0 }) return balance;

        foreach (Withdrawal withdrawal in block.Withdrawals)
        {
            if (withdrawal.Address != sender) continue;
            // Cannot underflow: the credit lands after execution and nothing spends it afterwards.
            if (UInt256.SubtractUnderflow(balance, withdrawal.AmountInWei, out balance)) return UInt256.Zero;
        }

        return balance;
    }
}
