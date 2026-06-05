// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Consensus.Validators;

public static class InclusionListValidator
{
    public static bool IsSatisfied(Block block, IAccountStateProvider state, IReleaseSpec spec)
    {
        if (!spec.InclusionListsEnabled) return true;
        Transaction[]? il = block.InclusionListTransactions;
        if (il is null) return false;

        // FOCIL is conditional: no gas left for a base-cost transfer → nothing is appendable.
        if (block.GasUsed + Transaction.BaseTxGasCost > block.GasLimit) return true;

        // Stack-allocate the inclusion bitmap when IL fits the spec cap; heap-alloc the unreachable
        // oversize path — engine API enforces the byte cap upstream, which bounds tx count.
        Span<bool> included = il.Length <= Eip7805Constants.MaxTransactionsPerInclusionList
            ? stackalloc bool[il.Length]
            : new bool[il.Length];

        // O(N+M) inclusion marking: hash → IL index, one dict lookup per block tx.
        Dictionary<Hash256, int> ilByHash = new(il.Length);
        for (int i = 0; i < il.Length; i++)
        {
            Hash256? h = il[i].Hash;
            if (h is not null) ilByHash[h] = i;
        }

        foreach (Transaction blockTx in block.Transactions)
        {
            if (blockTx.Hash is not null && ilByHash.TryGetValue(blockTx.Hash, out int idx))
                included[idx] = true;
        }

        // Dedup sender state reads: spam IL with many txs from one sender → 1 TryGetAccount.
        Dictionary<AddressAsKey, AccountStruct>? senderCache = null;
        for (int i = 0; i < il.Length; i++)
        {
            if (!included[i] && CouldIncludeTx(il[i], block, state, ref senderCache)) return false;
        }
        return true;
    }

    private static bool CouldIncludeTx(Transaction tx, Block block, IAccountStateProvider state, ref Dictionary<AddressAsKey, AccountStruct>? senderCache)
    {
        if (tx.SenderAddress is null) return false;
        if (block.GasUsed + tx.GasLimit > block.GasLimit) return false;

        // EIP-1559: compare baseFee against the cap (MaxFeePerGas), not the priority tip
        // (which is what tx.GasPrice exposes for type-2). Matches TransactionProcessor.
        if (tx.MaxFeePerGas < block.BaseFeePerGas) return false;

        senderCache ??= [];
        if (!senderCache.TryGetValue(tx.SenderAddress, out AccountStruct account))
        {
            if (!state.TryGetAccount(tx.SenderAddress, out account)) return false;
            senderCache[tx.SenderAddress] = account;
        }
        UInt256 txCost = tx.Value + (UInt256)tx.GasLimit * tx.MaxFeePerGas;
        return account.Balance >= txCost && account.Nonce == tx.Nonce;
    }
}
