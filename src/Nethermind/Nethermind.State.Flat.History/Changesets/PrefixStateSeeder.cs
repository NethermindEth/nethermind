// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>Lays an overlay over a world state standing at the previous block, through the same operations the
/// transactions used, so that what the target transaction then sees is what it saw when the block was processed.
/// Wipes go first and are committed on their own, so a re-created account is built on a clean slate rather than on
/// the remains of the one that was destroyed.</summary>
internal static class PrefixStateSeeder
{
    public static bool TryApply(MidBlockOverlay overlay, IWorldState state, IReleaseSpec spec)
    {
        if (!CodeIsResolvable(overlay, state)) return false;

        ApplyWipes(overlay, state);
        state.Commit(spec);
        ApplyAccounts(overlay, state, spec);
        ApplyStorage(overlay, state);
        state.Commit(spec);
        return true;
    }

    private static void ApplyWipes(MidBlockOverlay overlay, IWorldState state)
    {
        Dictionary<AddressAsKey, MidBlockOverlay.AccountOverlay>.Enumerator accounts = overlay.Accounts;
        while (accounts.MoveNext())
        {
            (AddressAsKey address, MidBlockOverlay.AccountOverlay account) = accounts.Current;
            bool wiped = account.Emptied || account.StorageClearedAt != MidBlockOverlay.NeverCleared;
            if (!wiped || !state.AccountExists(address)) continue;

            state.ClearStorage(address);
            if (account.Emptied) state.DeleteAccount(address);
        }
    }

    private static void ApplyAccounts(MidBlockOverlay overlay, IWorldState state, IReleaseSpec spec)
    {
        Dictionary<AddressAsKey, MidBlockOverlay.AccountOverlay>.Enumerator accounts = overlay.Accounts;
        while (accounts.MoveNext())
        {
            (AddressAsKey address, MidBlockOverlay.AccountOverlay account) = accounts.Current;
            if (account.Emptied && !account.Exists) continue;

            ApplyAccount(address, account, state, spec);
        }
    }

    private static void ApplyStorage(MidBlockOverlay overlay, IWorldState state)
    {
        Dictionary<StorageCell, MidBlockOverlay.StorageWrite>.Enumerator writes = overlay.Writes;
        while (writes.MoveNext())
        {
            (StorageCell cell, MidBlockOverlay.StorageWrite write) = writes.Current;
            if (overlay.TryGetAccount(cell.Address, out MidBlockOverlay.AccountOverlay? account))
            {
                if (account.Emptied && !account.Exists) continue;
                if (write.Transaction < account.StorageClearedAt) continue;
            }

            state.Set(cell, write.Value);
        }
    }

    private static void ApplyAccount(Address address, MidBlockOverlay.AccountOverlay account, IWorldState state, IReleaseSpec spec)
    {
        if (!state.AccountExists(address))
        {
            state.CreateAccount(address, account.Balance ?? UInt256.Zero, account.Nonce is { } created ? (ulong)created : 0);
        }
        else
        {
            if (account.Balance is { } balance) SetBalance(address, balance, state, spec);
            if (account.Nonce is { } nonce) state.SetNonce(address, (ulong)nonce);
        }

        if (account.CodeHash is not { } codeHash) return;

        byte[] code = codeHash == ValueKeccak.OfAnEmptyString ? [] : state.GetCode(codeHash)!;
        state.InsertCode(address, codeHash, code, spec);
    }

    private static void SetBalance(Address address, in UInt256 balance, IWorldState state, IReleaseSpec spec)
    {
        UInt256 current = state.GetBalance(address);
        if (balance > current) state.AddToBalance(address, balance - current, spec, out _);
        else if (balance < current) state.SubtractFromBalance(address, current - balance, spec, out _);
    }

    /// <summary>Code is content-addressed and never pruned, so a hash the node once executed always resolves; a miss
    /// means this state cannot stand in for the prefix and the trace must replay it.</summary>
    private static bool CodeIsResolvable(MidBlockOverlay overlay, IWorldState state)
    {
        Dictionary<AddressAsKey, MidBlockOverlay.AccountOverlay>.Enumerator accounts = overlay.Accounts;
        while (accounts.MoveNext())
        {
            MidBlockOverlay.AccountOverlay account = accounts.Current.Value;
            if (account.Emptied && !account.Exists) continue;
            if (account.CodeHash is { } codeHash && codeHash != ValueKeccak.OfAnEmptyString && !HasCode(state, codeHash)) return false;
        }

        return true;
    }

    private static bool HasCode(IWorldState state, in ValueHash256 codeHash)
    {
        try
        {
            return state.GetCode(codeHash) is not null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
