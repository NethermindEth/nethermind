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

    public static bool IsSatisfied(Block block, IReadOnlyStateProvider state, IReleaseSpec spec, ITxValidator txValidator, ulong maxVerifyGasPerTx = Eip8369Constants.MaxVerifyGasPerTx)
        => IsSatisfied(block, block.InclusionListTransactions, state, spec, txValidator, maxVerifyGasPerTx);

    /// <param name="maxVerifyGasPerTx">EIP-8369 <c>MAX_VERIFY_GAS_PER_TX</c>, above which a frame transaction is
    /// no Profile 2 candidate and its omission is excused; <c>0</c> lifts the cap.</param>
    public static bool IsSatisfied(Block block, Transaction[]? il, IReadOnlyStateProvider state, IReleaseSpec spec, ITxValidator txValidator, ulong maxVerifyGasPerTx = Eip8369Constants.MaxVerifyGasPerTx)
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
            return IsSatisfied(block, il, included, state, spec, txValidator, maxVerifyGasPerTx);
        }
        finally
        {
            if (rented is not null) ArrayPool<bool>.Shared.Return(rented);
        }
    }

    private static bool IsSatisfied(Block block, Transaction[] il, Span<bool> included, IReadOnlyStateProvider state, IReleaseSpec spec, ITxValidator txValidator, ulong maxVerifyGasPerTx)
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

        Dictionary<AddressAsKey, AccountStruct>? accountCache = null;
        for (int i = 0; i < il.Length; i++)
        {
            if (included[i]) continue;
            // EIP-8369: a frame transaction outside Profile 2 is enforced by no profile, so its omission is excused.
            if (il[i].SupportsFrames && Eip8369Profile2.Classify(il[i], maxVerifyGasPerTx) != Profile2Exclusion.None) continue;
            if (CouldIncludeTx(il[i], block, state, spec, txValidator, ref accountCache)) return false;
        }
        return true;
    }

    /// <summary>Whether an omitted inclusion-list entry could have been appended to <paramref name="block"/>.</summary>
    /// <remarks>Judges an EIP-8369 Profile 2 candidate on the conditions its frame layout makes determinate —
    /// the gas it would add, fee validity, and whether the payer its prefix nominates could cover the EIP-8141
    /// maximum cost. The account nonce and EIP-3607 are Profile 1 conditions and do not apply to it.</remarks>
    private static bool CouldIncludeTx(Transaction tx, Block block, IReadOnlyStateProvider state, IReleaseSpec spec, ITxValidator txValidator, ref Dictionary<AddressAsKey, AccountStruct>? accountCache)
    {
        if (tx.SenderAddress is null) return false;
        // Subtract on the block side: GasUsed <= GasLimit is invariant, so this cannot underflow the
        // way GasLimit - tx.GasLimit would for an oversized tx.
        if (tx.GasLimit > block.GasLimit - block.GasUsed) return false;
        // Appendability must match normal execution, so reuse the full well-formedness check, not a subset.
        if (!txValidator.IsWellFormed(tx, spec, block.GasLimit)) return false;
        if (tx.MaxFeePerGas < block.BaseFeePerGas) return false;

        // A sponsored frame transaction is paid by the payer its validation prefix nominates, not by its
        // sender. Always recomputed, never read from Transaction.PayerAddress, so the two cannot disagree.
        Address payer = tx.SupportsFrames ? FrameTxValidation.GetPrefixPaymaster(tx) ?? tx.SenderAddress : tx.SenderAddress;

        accountCache ??= [];
        ref AccountStruct account = ref CollectionsMarshal.GetValueRefOrAddDefault(accountCache, payer, out bool cached);
        // Cache the negative result too (default struct = balance 0, nonce 0, empty codehash).
        if (!cached) state.TryGetAccount(payer, out account);

        // A frame transaction reserves max_gas and its EIP-8141 maximum cost, both of which the forms below
        // under-count by at least the intrinsic cost; its sender is a smart account EIP-3607 does not bar.
        if (tx.SupportsFrames)
        {
            return FrameTxValidation.TryCalculateGasBudget(tx, spec, out _, out _, out ulong maxGas)
                   && maxGas <= block.GasLimit - block.GasUsed
                   && FrameTxValidation.TryCalculateMaxCost(tx, spec, out UInt256 frameCost)
                   && SpendableBalance(block, payer, in account) >= frameCost;
        }

        // EIP-3607: a sender with non-delegated code cannot send a tx.
        if (account.HasCode && !state.IsDelegatedCode(payer)) return false;

        // Overflow-checked like TransactionProcessor.BuyGas: an adversarial MaxFeePerGas must not wrap the cost.
        if (UInt256.MultiplyOverflow((UInt256)tx.GasLimit, tx.MaxFeePerGas, out UInt256 txCost)
            || UInt256.AddOverflow(txCost, tx.Value, out txCost))
            return false;

        // A blob tx must also cover maxFeePerBlobGas × blob gas up front, or it could never have executed.
        if (tx.SupportsBlobs
            && (!BlobGasCalculator.TryCalculateBlobMaxFee(tx.BlobVersionedHashes?.Length ?? 0, tx.MaxFeePerBlobGas ?? UInt256.Zero, out UInt256 blobFee)
                || UInt256.AddOverflow(txCost, blobFee, out txCost)))
            return false;

        return SpendableBalance(block, payer, in account) >= txCost && account.Nonce == tx.Nonce;
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
