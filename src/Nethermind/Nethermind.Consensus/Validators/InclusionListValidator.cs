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

namespace Nethermind.Consensus.Validators;

public static class InclusionListValidator
{
    public static bool IsSatisfied(Block block, IReadOnlyStateProvider state, IReleaseSpec spec)
        => IsSatisfied(block, block.InclusionListTransactions, state, spec);

    public static bool IsSatisfied(Block block, Transaction[]? il, IReadOnlyStateProvider state, IReleaseSpec spec)
    {
        if (!spec.InclusionListsEnabled) return true;
        // No IL attached = non-engine-API path (genesis, RLP import); IL doesn't apply.
        if (il is null) return true;

        // FOCIL is conditional: no gas left for a base-cost transfer → nothing is appendable.
        if (block.GasUsed + Transaction.BaseTxGasCost > block.GasLimit) return true;

        Span<bool> included = il.Length <= Eip7805Constants.MaxTransactionsPerInclusionList
            ? stackalloc bool[il.Length]
            : new bool[il.Length];

        // hash → first IL index. TryAdd preserves the first occurrence; on duplicates the later
        // entry stays unmarked but post-state appendability checks fail (nonce advanced).
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
            if (!included[i] && CouldIncludeTx(il[i], block, state, spec, ref senderCache)) return false;
        }
        return true;
    }

    private static bool CouldIncludeTx(Transaction tx, Block block, IReadOnlyStateProvider state, IReleaseSpec spec, ref Dictionary<AddressAsKey, AccountStruct>? senderCache)
    {
        if (tx.SenderAddress is null) return false;
        // Blob txs MUST NOT appear in an IL; cost formula here doesn't include blob gas anyway.
        if (tx.SupportsBlobs) return false;
        if (block.GasUsed + tx.GasLimit > block.GasLimit) return false;
        // A tx whose GasLimit is below the intrinsic cost cannot execute, so it isn't appendable.
        if (tx.GasLimit < (ulong)IntrinsicGasCalculator.Calculate(tx, spec, block.GasLimit)) return false;

        // EIP-1559: compare baseFee against the cap (MaxFeePerGas), not the priority tip
        // (which is what tx.GasPrice exposes for type-2). Matches TransactionProcessor.
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

        UInt256 txCost = tx.Value + (UInt256)tx.GasLimit * tx.MaxFeePerGas;
        return account.Balance >= txCost && account.Nonce == tx.Nonce;
    }
}
