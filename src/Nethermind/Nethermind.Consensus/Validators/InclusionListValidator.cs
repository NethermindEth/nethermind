// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Consensus.Eip8288;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Validators;

public static class InclusionListValidator
{
    private const int StackAllocEntries = 256;

    public static bool IsSatisfied(Block block, Transaction[]? il, IReadOnlyStateProvider state, IReleaseSpec spec, ITxValidator txValidator,
        RecursiveStark? proof, ILeanProofVerifier verifier, Func<Transaction, bool>? frameCanInclude = null)
    {
        if (!spec.InclusionListsEnabled) return true;
        // No IL attached = non-engine-API path (genesis, RLP import); IL doesn't apply.
        if (il is null) return true;
        // No room for even the cheapest possible tx → nothing is appendable.
        if (IsBlockFull(block, spec)) return true;

        if (spec.IsEip8288Enabled)
        {
            // An invalid FOCIL package establishes no mandatory transactions.
            if (!FocilInclusionListValidator.Validate(il, proof, verifier, out _, out _, spec)) return true;
        }

        // A conforming aggregate runs to tens of thousands of entries, far past what the stack can hold.
        bool[]? rented = il.Length > StackAllocEntries
            ? ArrayPool<bool>.Shared.Rent(il.Length)
            : null;
        try
        {
            Span<bool> included = rented is null ? stackalloc bool[il.Length] : rented.AsSpan(0, il.Length);
            included.Clear();
            return IsSatisfied(block, il, included, state, spec, txValidator, frameCanInclude);
        }
        finally
        {
            if (rented is not null) ArrayPool<bool>.Shared.Return(rented);
        }
    }

    public static bool IsBlockFull(Block block, IReleaseSpec spec)
    {
        ulong minIntrinsicGas = spec.IsEip2780Enabled ? GasCostOf.TransactionEip2780 : GasCostOf.Transaction;
        return block.GasUsed > block.GasLimit || minIntrinsicGas > block.GasLimit - block.GasUsed;
    }

    private static bool IsSatisfied(Block block, Transaction[] il, Span<bool> included, IReadOnlyStateProvider state, IReleaseSpec spec, ITxValidator txValidator, Func<Transaction, bool>? frameCanInclude)
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
            if (included[i] || il[i].SupportsFrames && il[i].Hash is { } hash && ilByHash.TryGetValue(hash, out int first) && included[first]) continue;
            if (il[i].SupportsFrames)
            {
                if (spec.IsEip8288Enabled && CouldIncludeFrameTx(il[i], block, spec, txValidator)
                    && (frameCanInclude ?? throw new InvalidOperationException("Frame inclusion lists require prefix simulation."))(il[i])) return false;
                continue;
            }
            if (CouldIncludeTx(il[i], block, state, spec, txValidator, ref senderCache)) return false;
        }
        return true;
    }

    private static bool CouldIncludeFrameTx(Transaction tx, Block block, IReleaseSpec spec, ITxValidator validator)
        => tx.SenderAddress is not null && FitsRemainingBlockGas(tx, block, spec, Eip8288Dependencies.RecursiveStarkGas(tx))
            && validator.IsWellFormed(tx, spec, block.GasLimit) && tx.MaxFeePerGas >= block.BaseFeePerGas
            && (!tx.CarriesBlobs || BlobGasCalculator.CalculateBlobGas(tx) <= spec.GasCosts.MaxBlobGasPerBlock - (block.Header.BlobGasUsed ?? 0)
                && BlobGasCalculator.TryCalculateFeePerBlobGas(block.Header, spec.BlobBaseFeeUpdateFraction, out UInt256 blobFee)
                && (tx.MaxFeePerBlobGas ?? UInt256.Zero) >= blobFee);

    private static bool CouldIncludeTx(Transaction tx, Block block, IReadOnlyStateProvider state, IReleaseSpec spec, ITxValidator txValidator, ref Dictionary<AddressAsKey, AccountStruct>? senderCache)
    {
        if (tx.SenderAddress is null) return false;
        if (!FitsRemainingBlockGas(tx, block, spec)) return false;
        // Appendability must match normal execution, so reuse the full well-formedness check, not a subset.
        if (!txValidator.IsWellFormed(tx, spec, block.GasLimit)) return false;
        if (tx.MaxFeePerGas < block.BaseFeePerGas) return false;
        if (tx.SupportsBlobs
            && (BlobGasCalculator.CalculateBlobGas(tx) > spec.GasCosts.MaxBlobGasPerBlock - (block.Header.BlobGasUsed ?? 0)
                || !BlobGasCalculator.TryCalculateFeePerBlobGas(block.Header, spec.BlobBaseFeeUpdateFraction, out UInt256 blobBaseFee)
                || (tx.MaxFeePerBlobGas ?? UInt256.Zero) < blobBaseFee))
            return false;

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

    /// <summary>Whether an appended transaction's worst-case block gas still fits.</summary>
    /// <remarks>EIP-8037 admits a transaction only if both its execution and its state reservation fit the
    /// matching dimension, so measuring it against the header's max(execution, state) rejects transactions the
    /// spec judges includable. Callers whose block carries no dimensions must not put an EIP-8037 block to this
    /// check — the max alone under-reports censorship — which is why the engine API declines to answer instead.</remarks>
    private static bool FitsRemainingBlockGas(Transaction tx, Block block, IReleaseSpec spec, ulong additionalGas = 0)
    {
        // Subtract on the block side: GasUsed <= GasLimit is invariant, so this cannot underflow the
        // way GasLimit - tx.GasLimit would for an oversized tx.
        if (!spec.IsEip8037Enabled) return tx.GasLimit <= block.GasLimit - block.GasUsed
            && additionalGas <= block.GasLimit - block.GasUsed - tx.GasLimit;

        (ulong execution, ulong state) = block.Header.GasUsedPerDimension ?? (block.GasUsed, block.GasUsed);
        if (spec.IsEip8288Enabled && block.Header.GasUsedPerDimension is not null)
        {
            ulong proofGas = (ulong)Eip8288Dependencies.DependencyDeclarationCount(block) * Eip8288Constants.LeanStarkVerificationGas;
            execution += proofGas;
            state += proofGas;
        }
        return Eip8037BlockGasInclusionCheck.TryGetBlockGasReservations(tx, spec, out ulong executionReservation, out ulong stateReservation)
            && Eip8037BlockGasInclusionCheck.Validate(block.GasLimit, execution, state, executionReservation, stateReservation)
                == Eip8037BlockGasInclusionCheck.Outcome.Ok
            && additionalGas <= block.GasLimit - execution - executionReservation
            && additionalGas <= block.GasLimit - state - stateReservation;
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
