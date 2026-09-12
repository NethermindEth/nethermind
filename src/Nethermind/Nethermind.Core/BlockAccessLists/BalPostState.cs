// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Computes the post-block account fields implied by a BAL account row, mirroring the semantics
/// of the canonical per-account BAL replay (<c>BlockAccessListManager.ApplyStateChanges</c>).
/// </summary>
public static class BalPostState
{
    /// <summary>
    /// Computes the post-block account for <paramref name="changes"/> applied on top of
    /// <paramref name="parent"/>; <c>null</c> means the account must be absent post-block.
    /// </summary>
    /// <remarks>
    /// Each account field takes the value of the last recorded change, falling back to the
    /// parent's value when that field has no changes. A row with no field changes leaves the
    /// account untouched (<paramref name="parent"/> is returned as-is, including <c>null</c>):
    /// storage reads are observations, not mutations. The returned account keeps the parent's
    /// storage root — recomputing it from the BAL's storage writes is the applier's job.
    /// Per EIP-158, a touched account that ends the block totally empty (zero nonce, zero
    /// balance, no code) must be absent from the post state, which also implies its storage
    /// is cleared.
    /// </remarks>
    public static Account? Compute(Account? parent, ReadOnlyAccountChanges changes, IReleaseSpec spec)
    {
        bool hasBalanceChange = changes.BalanceChanges.Length > 0;
        bool hasNonceChange = changes.NonceChanges.Length > 0;
        bool hasCodeChange = changes.CodeChanges.Length > 0;
        if (!hasBalanceChange && !hasNonceChange && !hasCodeChange)
        {
            return parent;
        }

        UInt256 balance = hasBalanceChange ? changes.BalanceChanges[^1].Value : parent?.Balance ?? UInt256.Zero;
        ulong nonce = hasNonceChange ? changes.NonceChanges[^1].Value : parent?.Nonce ?? 0;
        // CodeChange.CodeHash is the zero hash (not Keccak.OfAnEmptyString) when Code is null;
        // normalize so null and empty code agree on "no code" — otherwise a null-code change would
        // dodge the EIP-158 empty check below and diverge from the canonical InsertCode path.
        Hash256 codeHash = hasCodeChange
            ? changes.CodeChanges[^1].Code is null ? Keccak.OfAnEmptyString : new Hash256(changes.CodeChanges[^1].CodeHash)
            : parent?.CodeHash ?? Keccak.OfAnEmptyString;

        // EIP-158: a touched, totally empty account (zero nonce, zero balance, no code) must be
        // absent from the post-block state.
        if (spec.IsEip158Enabled && balance.IsZero && nonce == 0 && codeHash == Keccak.OfAnEmptyString)
        {
            return null;
        }

        return new Account(nonce, balance, parent?.StorageRoot ?? Keccak.EmptyTreeHash, codeHash);
    }
}
