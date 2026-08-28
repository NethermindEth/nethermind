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
    private const int StackAllocEntries = 256;

    public static bool IsSatisfied(Block block, IReadOnlyStateProvider state, IReleaseSpec spec, ITxValidator txValidator)
        => IsSatisfied(block, block.InclusionListTransactions, state, spec, txValidator);

    public static bool IsSatisfied(Block block, Transaction[]? il, IReadOnlyStateProvider state, IReleaseSpec spec, ITxValidator txValidator)
    {
        if (!spec.InclusionListsEnabled) return true;
        // No IL attached = non-engine-API path (genesis, RLP import); IL doesn't apply.
        if (il is null) return true;

        // No room for even the cheapest possible tx → nothing is appendable.
        ulong minIntrinsicGas = spec.IsEip2780Enabled ? GasCostOf.TransactionEip2780 : GasCostOf.Transaction;
        if (block.GasUsed + minIntrinsicGas > block.GasLimit) return true;

        // A conforming aggregate runs to tens of thousands of entries, far past what the stack can hold.
        bool[]? rented = il.Length > StackAllocEntries
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
            if (included[i]) continue;
            // The rules below judge appendability on the account nonce, which a frame transaction does not
            // use (EIP-8369 Profile 2), so reading one through them reports an honest payload as censoring.
            if (il[i].SupportsFrames) continue;
            if (CouldIncludeTx(il[i], block, state, spec, txValidator, ref senderCache)) return false;
        }
        return true;
    }

    private static bool CouldIncludeTx(Transaction tx, Block block, IReadOnlyStateProvider state, IReleaseSpec spec, ITxValidator txValidator, ref Dictionary<AddressAsKey, AccountStruct>? senderCache)
    {
        if (tx.SenderAddress is null) return false;
        // Subtract on the block side: GasUsed <= GasLimit is invariant, so this cannot underflow the
        // way GasLimit - tx.GasLimit would for an oversized tx.
        if (tx.GasLimit > block.GasLimit - block.GasUsed) return false;
        // Appendability must match normal execution, so reuse the full well-formedness check, not a subset.
        if (!txValidator.IsWellFormed(tx, spec, block.GasLimit)) return false;
        if (tx.MaxFeePerGas < block.BaseFeePerGas) return false;

        senderCache ??= [];
        ref AccountStruct account = ref CollectionsMarshal.GetValueRefOrAddDefault(senderCache, tx.SenderAddress, out bool cached);
        // Cache the negative result too (default struct = balance 0, nonce 0, empty codehash).
        if (!cached) state.TryGetAccount(tx.SenderAddress, out account);

        // EIP-3607: a sender with non-delegated code cannot send a tx.
        if (account.HasCode && !state.IsDelegatedCode(tx.SenderAddress)) return false;

        // Overflow-checked like TransactionProcessor.BuyGas: an adversarial MaxFeePerGas must not wrap the cost.
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

    /// <summary>Balance the sender would have had when an appended transaction executed.</summary>
    /// <remarks>Withdrawals are the only post-merge credit applied after the block's transactions, so counting
    /// them into the post-block balance would make an honest proposer look like a censor.</remarks>
    private static UInt256 SpendableBalance(Block block, Address sender, ref readonly AccountStruct account)
    {
        UInt256 balance = account.Balance;
        if (block.Withdrawals is not { Length: > 0 }) return balance;

        foreach (Withdrawal withdrawal in block.Withdrawals)
        {
            if (withdrawal.Address != sender) continue;
            if (UInt256.SubtractUnderflow(balance, withdrawal.AmountInWei, out balance)) return UInt256.Zero;
        }

        return balance;
    }
}
