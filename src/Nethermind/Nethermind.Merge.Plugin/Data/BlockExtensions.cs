// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.State.Proofs;

namespace Nethermind.Merge.Plugin.Data;

public static class BlockExtensions
{
    public static void CalculateTrieRoots(this Block block)
    {
        block.Header.TxRoot = TxTrie.CalculateRoot(block.Transactions);
        block.Header.WithdrawalsRoot = block.Withdrawals is null ? null : new WithdrawalTrie(block.Withdrawals).RootHash;
    }
}
