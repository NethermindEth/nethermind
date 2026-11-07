// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Validators;

public static class InclusionListValidator
{
    private const int StackAllocEntries = 256;

    public static bool IsSatisfied(Block block, IReadOnlyStateProvider state, IReleaseSpec spec, ITxValidator txValidator)
        => IsSatisfied(block, block.InclusionListTransactions, state, spec, txValidator, block.InclusionLists);

    /// <summary>Checks appendability against state after transactions, before withdrawals and request system calls.</summary>
    /// <param name="lists">The counted lists with their resolved EIP-8288 proofs, or <c>null</c> when only the flattened
    /// <paramref name="il"/> was supplied, which then binds only its transactions without dependencies.</param>
    public static bool IsSatisfied(Block block, Transaction[]? il, IReadOnlyStateProvider state, IReleaseSpec spec, ITxValidator txValidator,
        IReadOnlyList<CommitteeInclusionList>? lists, Func<Transaction, bool>? frameCanInclude = null)
    {
        if (!spec.InclusionListsEnabled) return true;
        // No IL attached = non-engine-API path (genesis, RLP import); IL doesn't apply.
        if (il is null) return true;
        // No room for even the cheapest possible tx → nothing is appendable.
        if (IsBlockFull(block, spec)) return true;

        if (spec.IsEip8288Enabled)
        {
            il = InclusionListProofValidator.SelectEligible(il, lists, spec);
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

    /// <summary>Whether <paramref name="block"/> leaves out an EIP-8141 frame entry of its inclusion list that
    /// <see cref="IsSatisfied(Block, Transaction[], IReadOnlyStateProvider, IReleaseSpec, ITxValidator, IReadOnlyList{CommitteeInclusionList}, Func{Transaction, bool})"/>
    /// may have to judge by prefix simulation.</summary>
    /// <remarks>Decided from transaction hashes alone, before execution, and conservatively: an entry is left out unless the block
    /// carries a transaction of its hash, whether or not it is eligible. When none is, the simulation is never asked for.</remarks>
    public static bool OmitsFrameEntry(Block block)
    {
        if (block.InclusionListTransactions is not { } il) return false;
        HashSet<Hash256>? included = null;
        foreach (Transaction tx in il)
        {
            if (!tx.SupportsFrames || tx.SupportsBlobs || tx.CarriesBlobs) continue;
            if (tx.Hash is null) return true;
            if (included is null)
            {
                included = new(block.Transactions.Length);
                foreach (Transaction blockTx in block.Transactions)
                    if (blockTx.Hash is { } hash) included.Add(hash);
            }
            if (!included.Contains(tx.Hash)) return true;
        }
        return false;
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
            // EIP-7805 excludes blob transactions from the appendability check; a list carries no sidecar for frame blobs either.
            if (il[i].SupportsBlobs || il[i].CarriesBlobs) continue;
            if (il[i].SupportsFrames)
            {
                // EIP-8288 adds no omission grounds: no block dependency quota, local proving or wrapper limits.
                if (!spec.IsEip8288Enabled || !CouldIncludeFrameTx(il[i], block, spec, txValidator)) continue;
                if ((frameCanInclude ?? throw new InvalidOperationException("Frame inclusion lists require prefix simulation."))(il[i])) return false;
                continue;
            }
            if (CouldIncludeTx(il[i], block, state, spec, txValidator, ref senderCache)) return false;
        }
        return true;
    }

    private static bool CouldIncludeFrameTx(Transaction tx, Block block, IReleaseSpec spec, ITxValidator validator)
        => tx.SenderAddress is not null && FitsRemainingBlockGas(tx, block, spec) && validator.IsWellFormed(tx, spec, block.GasLimit)
            && FrameTxValidation.CoversRequiredMaxFee(tx, spec, block.BaseFeePerGas, UInt256.Zero);

    private static bool CouldIncludeTx(Transaction tx, Block block, IReadOnlyStateProvider state, IReleaseSpec spec, ITxValidator txValidator, ref Dictionary<AddressAsKey, AccountStruct>? senderCache)
    {
        if (tx.SenderAddress is null) return false;
        if (!FitsRemainingBlockGas(tx, block, spec)) return false;
        // Appendability must match normal execution, so reuse the full well-formedness check, not a subset.
        if (!txValidator.IsWellFormed(tx, spec, block.GasLimit)) return false;
        if (tx.MaxFeePerGas < block.BaseFeePerGas) return false;

        senderCache ??= [];
        ref AccountStruct account = ref CollectionsMarshal.GetValueRefOrAddDefault(senderCache, tx.SenderAddress, out bool cached);
        // Cache the negative result too (default struct = balance 0, nonce 0, empty codehash).
        if (!cached) state.TryGetAccount(tx.SenderAddress, out account);

        // EIP-3607: a sender with non-delegated code cannot send a tx.
        if (account.HasCode && !state.IsDelegatedCode(account.CodeHash)) return false;

        // Overflow-checked like TransactionProcessor.BuyGas: an adversarial MaxFeePerGas must not wrap the cost.
        if (UInt256.MultiplyOverflow((UInt256)tx.GasLimit, tx.MaxFeePerGas, out UInt256 txCost)
            || UInt256.AddOverflow(txCost, tx.Value, out txCost))
            return false;

        return account.Balance >= txCost && account.Nonce == tx.Nonce;
    }

    /// <summary>Whether an appended transaction's worst-case block gas still fits.</summary>
    /// <remarks>EIP-8037 admits a transaction only if both its execution and its state reservation fit the
    /// matching dimension, so measuring it against the header's max(execution, state) rejects transactions the
    /// spec judges includable. Callers whose block carries no dimensions must not put an EIP-8037 block to this
    /// check — the max alone under-reports censorship — which is why the engine API declines to answer instead.</remarks>
    private static bool FitsRemainingBlockGas(Transaction tx, Block block, IReleaseSpec spec)
    {
        // Subtract on the block side: GasUsed <= GasLimit is invariant, so this cannot underflow the
        // way GasLimit - tx.GasLimit would for an oversized tx.
        if (!spec.IsEip8037Enabled) return tx.GasLimit <= block.GasLimit - block.GasUsed;

        (ulong execution, ulong state) = block.Header.GasUsedPerDimension ?? (block.GasUsed, block.GasUsed);
        return Eip8037BlockGasInclusionCheck.TryGetBlockGasReservations(tx, spec, out ulong executionReservation, out ulong stateReservation)
            && Eip8037BlockGasInclusionCheck.Validate(block.GasLimit, execution, state, executionReservation, stateReservation)
                == Eip8037BlockGasInclusionCheck.Outcome.Ok;
    }

}
