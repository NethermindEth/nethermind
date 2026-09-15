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

        if (account.Emptied && !account.Exists)
        {
            overlaid = null;
            return true;
        }

        Account basis = account.Emptied ? Account.TotallyEmpty : underlying ?? Account.TotallyEmpty;
        bool wiped = account.StorageClearedAt != MidBlockOverlay.NeverCleared;
        overlaid = new Account(
            account.Nonce is { } nonce ? (ulong)nonce : (ulong)basis.Nonce,
            account.Balance ?? basis.Balance,
            wiped ? Keccak.EmptyTreeHash : basis.StorageRoot,
            account.CodeHash is { } codeHash ? (Hash256)codeHash : basis.CodeHash);
        return true;
    }

    public bool TryGetStorage(Address address, in UInt256 index, out UInt256 value) => overlay.TryGetStorage(new StorageCell(address, index), out value);

    public bool HasStorage(Address address) => overlay.HasStorage(address);
}
