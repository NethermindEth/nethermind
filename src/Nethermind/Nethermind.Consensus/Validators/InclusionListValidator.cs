// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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

        Span<bool> included = il.Length <= Eip7805Constants.MaxTransactionsPerInclusionList
            ? stackalloc bool[il.Length]
            : new bool[il.Length];

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
        // Blob txs MUST NOT appear in an IL.
        if (tx.SupportsBlobs) return false;
        if (block.GasUsed + tx.GasLimit > block.GasLimit) return false;
        // Appendability must match normal execution: reuse the block validator's well-formedness
        // check (intrinsic gas, typed-tx rules, e.g. maxPriorityFeePerGas <= maxFeePerGas) instead of a subset.
        if (!txValidator.IsWellFormed(tx, spec, block.GasLimit)) return false;
        if (tx.MaxFeePerGas < block.BaseFeePerGas) return false;

        senderCache ??= [];
        if (!senderCache.TryGetValue(tx.SenderAddress, out AccountStruct account))
        {
            // Cache the negative result too (default struct = balance 0, nonce 0, empty codehash).
            state.TryGetAccount(tx.SenderAddress, out account);
            senderCache[tx.SenderAddress] = account;
        }

        // EIP-3607: a sender with non-delegated code cannot send a tx.
        if (account.HasCode && !state.IsDelegatedCode(tx.SenderAddress)) return false;

        // Mirror TransactionProcessor.BuyGas: overflow-checked so an adversarial MaxFeePerGas can't wrap the cost below the balance.
        if (UInt256.MultiplyOverflow((UInt256)tx.GasLimit, tx.MaxFeePerGas, out UInt256 txCost)
            || UInt256.AddOverflow(txCost, tx.Value, out txCost))
            return false;
        return account.Balance >= txCost && account.Nonce == tx.Nonce;
    }
}
