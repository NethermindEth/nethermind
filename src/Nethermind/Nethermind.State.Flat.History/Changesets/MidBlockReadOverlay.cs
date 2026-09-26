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

        overlaid = Overlay(address, account, account.Emptied ? Account.TotallyEmpty : underlying ?? Account.TotallyEmpty);
        return true;
    }

    /// <summary>An account the prefix emptied stands on the empty account, not on the one in the state underneath, so
    /// it is answered without reading it - one random archive read saved per such account on the trace path.</summary>
    public bool TryGetAccountWithoutBasis(Address address, out Account? overlaid)
    {
        overlaid = null;
        if (!overlay.TryGetAccount(address, out MidBlockOverlay.AccountOverlay? account) || !account.Emptied) return false;

        overlaid = Overlay(address, account, Account.TotallyEmpty);
        return true;
    }

    private Account? Overlay(Address address, MidBlockOverlay.AccountOverlay account, Account basis)
    {
        if (account.Emptied && !account.Exists) return null;

        return new Account(
            account.Nonce is { } nonce ? (ulong)nonce : (ulong)basis.Nonce,
            account.Balance ?? basis.Balance,
            StorageRootOf(address, account, basis),
            account.CodeHash is { } codeHash ? (Hash256)codeHash : basis.CodeHash);
    }

    /// <summary>The root says whether the account holds storage, not what the root is. A wipe empties it only if
    /// nothing was written afterwards, and an account the prefix re-created holds whatever it has written since,
    /// even though its basis is the empty account: reporting empty there would have a later wipe skipped and the
    /// account's old slots read back instead of zero.</summary>
    private Hash256 StorageRootOf(Address address, MidBlockOverlay.AccountOverlay account, Account basis)
    {
        bool holdsStorage = overlay.HasStorageWrites(address);
        if (!holdsStorage && account.StorageClearedAt != MidBlockOverlay.NeverCleared) return Keccak.EmptyTreeHash;

        return holdsStorage && basis.StorageRoot == Keccak.EmptyTreeHash ? IStateReadOverlay.NonEmptyStorageRoot : basis.StorageRoot;
    }

    public bool TryGetStorage(Address address, in UInt256 index, out UInt256 value) => overlay.TryGetStorage(new StorageCell(address, index), out value);

    public bool HasStorage(Address address) => overlay.HasStorage(address);
}
