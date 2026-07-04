// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>Final post-block state changes reduced from a block access list.</summary>
/// <remarks>
/// Each changed field/slot is collapsed to its last-indexed post-value; the change arrays are
/// strictly ascending by index (the RLP decoders enforce this), so the final element wins.
/// Accounts touched-but-unchanged (<see cref="ReadOnlyAccountChanges.HasStateChanges"/> false)
/// are excluded - they must not dirty the trie. Zero storage values are kept: zero means delete.
/// </remarks>
public sealed class BalPostStateDelta
{
    /// <summary>Final state of a single account after reduction.</summary>
    public readonly struct AccountDelta
    {
        /// <summary>Account address.</summary>
        public Address Address { get; init; }

        /// <summary>Last balance post-value, or null when the balance was unchanged.</summary>
        public UInt256? Balance { get; init; }

        /// <summary>Last nonce post-value, or null when the nonce was unchanged.</summary>
        public ulong? Nonce { get; init; }

        /// <summary>Last code hash, or null when the code was unchanged.</summary>
        public ValueHash256? CodeHash { get; init; }

        /// <summary>One entry per changed slot with its final value (zeros included).</summary>
        public SlotWrite[] Storage { get; init; }
    }

    /// <summary>Final post-value for a single storage slot.</summary>
    public readonly record struct SlotWrite(UInt256 Slot, EvmWord Value);

    /// <summary>Accounts with state changes only.</summary>
    public AccountDelta[] Accounts { get; }

    private BalPostStateDelta(AccountDelta[] accounts) => Accounts = accounts;

    /// <summary>Reduces a block access list to its final post-block state delta.</summary>
    /// <param name="bal">The block access list to reduce.</param>
    /// <returns>The reduced post-state delta.</returns>
    /// <remarks>
    /// Requires every change array (balance/nonce/code and per-slot) to be strictly ascending by
    /// <see cref="IIndexedChange.Index"/>, as the RLP decoders guarantee - last-element-wins is only
    /// correct under that invariant. <see cref="ReadOnlyAccountChanges"/> has an unvalidated public
    /// ctor, so the invariant is enforced here at runtime.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A change array is not strictly ascending by index.</exception>
    public static BalPostStateDelta Reduce(ReadOnlyBlockAccessList bal)
    {
        List<AccountDelta> accounts = [];
        foreach (ReadOnlyAccountChanges ac in bal.AccountChanges)
        {
            if (!ac.HasStateChanges) continue; // read-only entries never dirty the trie

            RequireAscending(ac.BalanceChanges);
            RequireAscending(ac.NonceChanges);
            RequireAscending(ac.CodeChanges);

            SlotWrite[] storage = new SlotWrite[CountNonEmpty(ac.StorageChanges)];
            int w = 0;
            foreach (ReadOnlySlotChanges sc in ac.StorageChanges)
            {
                if (sc.Changes.Length == 0) continue; // defensive; decoder rejects empty
                RequireAscending(sc.Changes);
                storage[w++] = new SlotWrite(sc.Key, sc.Changes[^1].Value); // zero value kept: means trie delete
            }

            UInt256? balance = ac.BalanceChanges.Length > 0 ? ac.BalanceChanges[^1].Value : null;
            ulong? nonce = ac.NonceChanges.Length > 0 ? ac.NonceChanges[^1].Value : null;
            // Explicit (ValueHash256?) null branch: ValueHash256 has an implicit operator from
            // Hash256? so an untyped null lifts to default(ValueHash256) with HasValue=true.
            ValueHash256? codeHash = ac.CodeChanges.Length > 0 ? ac.CodeChanges[^1].CodeHash : (ValueHash256?)null;

            // HasStateChanges can be true while every slot change array is empty (unvalidated ctor);
            // such an account contributes nothing, so drop it to honour the state-changes-only contract.
            if (balance is null && nonce is null && codeHash is null && storage.Length == 0) continue;

            accounts.Add(new AccountDelta
            {
                Address = ac.Address,
                Balance = balance,
                Nonce = nonce,
                CodeHash = codeHash,
                Storage = storage,
            });
        }

        return new BalPostStateDelta(accounts.ToArray());
    }

    private static int CountNonEmpty(ReadOnlySlotChanges[] storageChanges)
    {
        int count = 0;
        foreach (ReadOnlySlotChanges sc in storageChanges)
        {
            if (sc.Changes.Length > 0) count++;
        }
        return count;
    }

    private static void RequireAscending<T>(T[] changes) where T : IIndexedChange
    {
        for (int i = 1; i < changes.Length; i++)
        {
            if (changes[i - 1].Index >= changes[i].Index)
            {
                throw new InvalidOperationException("BAL change indices must be strictly ascending");
            }
        }
    }
}
