// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>Reads through a block prefix: an account the prefix touched is the parent's account with the changed
/// fields laid over it, the empty account if the prefix destroyed and re-created it, gone if it destroyed it; a slot of
/// an account the prefix wiped is zero unless written after the wipe. Nothing here touches the state underneath.</summary>
internal sealed class MidBlockReadOverlay(MidBlockOverlay overlay) : IStateReadOverlay
{
    public bool TryGetAccount(Address address, Account? underlying, out Account? overlaid)
    {
        if (!overlay.TryGetAccount(address, out MidBlockOverlay.AccountOverlay? account))
        {
            overlaid = null;
            return false;
        }

        overlaid = Overlay(account, account.Emptied ? Account.TotallyEmpty : underlying ?? Account.TotallyEmpty);
        return true;
    }

    /// <summary>An account the prefix emptied stands on the empty account, not on the one in the state underneath, so
    /// it is answered without reading it - one random archive read saved per such account on the trace path.</summary>
    public bool TryGetAccountWithoutBasis(Address address, out Account? overlaid)
    {
        overlaid = null;
        if (!overlay.TryGetAccount(address, out MidBlockOverlay.AccountOverlay? account) || !account.Emptied) return false;

        overlaid = Overlay(account, Account.TotallyEmpty);
        return true;
    }

    private static Account? Overlay(MidBlockOverlay.AccountOverlay account, Account basis)
    {
        if (account.Emptied && !account.Exists) return null;

        bool wiped = account.StorageClearedAt != MidBlockOverlay.NeverCleared;
        return new Account(
            account.Nonce is { } nonce ? (ulong)nonce : (ulong)basis.Nonce,
            account.Balance ?? basis.Balance,
            wiped ? Keccak.EmptyTreeHash : basis.StorageRoot,
            account.CodeHash is { } codeHash ? (Hash256)codeHash : basis.CodeHash);
    }

    public bool TryGetStorage(Address address, in UInt256 index, out UInt256 value) => overlay.TryGetStorage(new StorageCell(address, index), out value);

    public bool HasStorage(Address address) => overlay.HasStorage(address);
}
