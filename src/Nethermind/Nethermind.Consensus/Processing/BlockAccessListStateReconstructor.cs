// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing;

/// <summary>Applies EIP-7928 post-block values without executing transactions.</summary>
/// <remarks>The caller must establish consensus finality and verify the resulting state root before persisting it.</remarks>
public static class BlockAccessListStateReconstructor
{
    /// <summary>Applies the last write of each changed account field and storage slot.</summary>
    /// <remarks>Read-only accounts are left untouched. State commit and root verification belong to the caller.</remarks>
    public static void Apply(IWorldState state, ReadOnlyBlockAccessList accessList, IReleaseSpec spec, CancellationToken token = default)
    {
        foreach (ReadOnlyAccountChanges account in accessList.AccountChanges)
        {
            token.ThrowIfCancellationRequested();
            if (!account.HasStateChanges) continue;

            Address address = account.Address;
            state.CreateAccountIfNotExists(address, UInt256.Zero);

            if (account.BalanceChanges.Length > 0)
            {
                UInt256 balance = account.BalanceChanges[^1].Value;
                UInt256 current = state.GetBalance(address);
                if (balance > current)
                    state.AddToBalance(address, balance - current, spec);
                else if (balance < current)
                    state.SubtractFromBalance(address, current - balance, spec);
            }

            if (account.NonceChanges.Length > 0)
                state.SetNonce(address, checked((ulong)account.NonceChanges[^1].Value));

            if (account.CodeChanges.Length > 0)
            {
                CodeChange code = account.CodeChanges[^1];
                state.InsertCode(address, code.CodeHash, code.Code, spec);
            }

            foreach (ReadOnlySlotChanges slot in account.StorageChanges)
            {
                if (slot.Changes.Length > 0)
                    state.Set(new StorageCell(address, slot.Key), slot.Changes[^1].Value);
            }
        }
    }
}
