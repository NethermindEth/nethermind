// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>What one block access list does to one account's state.</summary>
/// <remarks>
/// <para>
/// Every writer that applies a list to state consumes this, so they all agree on the result. EIP-161 removes an
/// account left empty by a transaction, so emptiness is checked after the changes at each block access index, as
/// execution does, and once more at the end of the list for an account whose fields the list did not change. A
/// removed account loses its storage, and only the changes after its last removal survive.
/// </para>
/// <para>
/// Post-merge no account with storage is ever empty (EIP-7523), so on a real chain this gives the same state as
/// checking only the final account.
/// </para>
/// </remarks>
public readonly struct BalAccountDelta
{
    private static readonly ValueHash256 EmptyCodeHash = Keccak.OfAnEmptyString.ValueHash256;

    // Slot changes at or below this index were wiped; meaningful only when WipesStorage.
    private readonly uint _lastWipe;

    private BalAccountDelta(ReadOnlyAccountChanges changes, Account? account, bool wipesStorage, uint lastWipe, CodeChange? code)
    {
        Changes = changes;
        Account = account;
        WipesStorage = wipesStorage;
        _lastWipe = lastWipe;
        Code = code;
    }

    /// <summary>The list's changes to the account.</summary>
    public ReadOnlyAccountChanges Changes { get; }

    /// <summary>The account after the list, or <see langword="null"/> when it does not exist afterwards.</summary>
    public Account? Account { get; }

    /// <summary>
    /// Whether the account was removed at some point in the list, dropping the storage it held before and any slot
    /// it wrote before the removal.
    /// </summary>
    public bool WipesStorage { get; }

    /// <summary>The change that set the account's final code, or <see langword="null"/> when its code is unchanged.</summary>
    public CodeChange? Code { get; }

    /// <summary>Whether the list changed the balance, nonce or code, rather than only writing slots.</summary>
    public bool ChangesAccount =>
        Changes.BalanceChanges.Length > 0 || Changes.NonceChanges.Length > 0 || Changes.CodeChanges.Length > 0;

    /// <summary>Gets the value <paramref name="slot"/> holds after the list, if the list leaves it written.</summary>
    /// <returns>
    /// <see langword="false"/> when the slot has no changes, or its last change preceded a removal of the account; a
    /// wiped slot reads as zero.
    /// </returns>
    public bool TryGetFinalValue(ReadOnlySlotChanges slot, out UInt256 value)
    {
        StorageChange[] changes = slot.Changes;
        if (Account is null || changes.Length == 0 || (WipesStorage && changes[^1].Index <= _lastWipe))
        {
            value = default;
            return false;
        }

        value = changes[^1].Value;
        return true;
    }

    /// <summary>Computes what <paramref name="changes"/> does to <paramref name="account"/>.</summary>
    /// <param name="account">The account before the list, or <see langword="null"/> if it does not exist.</param>
    /// <param name="changes">The list's changes to the account.</param>
    public static BalAccountDelta Compute(Account? account, ReadOnlyAccountChanges changes)
    {
        BalanceChange[] balances = changes.BalanceChanges;
        NonceChange[] nonces = changes.NonceChanges;
        CodeChange[] codes = changes.CodeChanges;
        Account image = account ?? Account.TotallyEmpty;

        UInt256 balance = image.Balance;
        ulong nonce = image.Nonce;
        bool hasCode = image.HasCode;
        bool wiped = false;
        uint lastWipe = 0;
        CodeChange? code = null;

        // Fields are tracked as values so a hot account costs one allocation, not one per transaction.
        int b = 0, n = 0, c = 0;
        while (b < balances.Length || n < nonces.Length || c < codes.Length)
        {
            uint index = uint.MaxValue;
            if (b < balances.Length) index = Math.Min(index, balances[b].Index);
            if (n < nonces.Length) index = Math.Min(index, nonces[n].Index);
            if (c < codes.Length) index = Math.Min(index, codes[c].Index);

            if (b < balances.Length && balances[b].Index == index) balance = balances[b++].Value;
            if (n < nonces.Length && nonces[n].Index == index) nonce = nonces[n++].Value;
            if (c < codes.Length && codes[c].Index == index)
            {
                code = codes[c++];
                hasCode = code.Value.CodeHash != EmptyCodeHash;
            }

            if (balance.IsZero && nonce == 0 && !hasCode)
            {
                wiped = true;
                lastWipe = index;
                code = null;
            }
        }

        // Also reached by a list that only writes slots of an empty or missing account.
        if (balance.IsZero && nonce == 0 && !hasCode) return new(changes, null, true, uint.MaxValue, null);

        // A wipe started the account over from empty, so its prior storage and code are gone.
        Account result = !wiped && balances.Length == 0 && nonces.Length == 0 && codes.Length == 0
            ? image
            : new Account(nonce, balance,
                wiped ? Keccak.EmptyTreeHash : image.StorageRoot,
                code?.CodeHash.ToCommitment() ?? (wiped ? Keccak.OfAnEmptyString : image.CodeHash));

        return new(changes, result, wiped, lastWipe, code);
    }

    /// <summary>Whether <paramref name="changes"/> can leave an account empty after some index, whatever its prior state.</summary>
    /// <remarks>
    /// Emptiness is checked only after an index with an account change, so one of those changes must set a zero balance
    /// or nonce, or empty code. Lets a caller that only needs to know whether a surviving account was wiped skip
    /// <see cref="Compute"/>.
    /// </remarks>
    public static bool CanEmpty(ReadOnlyAccountChanges changes)
    {
        foreach (BalanceChange change in changes.BalanceChanges)
        {
            if (change.Value.IsZero) return true;
        }

        foreach (NonceChange change in changes.NonceChanges)
        {
            if (change.Value == 0) return true;
        }

        foreach (CodeChange change in changes.CodeChanges)
        {
            if (change.CodeHash == EmptyCodeHash) return true;
        }

        return false;
    }

    /// <summary>Gets the bytecode for <paramref name="change"/> from the change itself or, when adopted, from <paramref name="bal"/>.</summary>
    /// <returns>
    /// <see langword="false"/> for an EIP-8298 adoption of code the list did not deposit: its bytecode should already be
    /// in the code database.
    /// </returns>
    public static bool TryGetCode(ReadOnlyBlockAccessList bal, in CodeChange change, [NotNullWhen(true)] out byte[]? code)
    {
        if (!change.IsAdopted)
        {
            code = change.Code;
            return true;
        }

        // A deposit can come from an account whose final code differs, so it is looked up across all changes.
        if (bal.GetCodeChangesByHash()?.TryGetValue(change.CodeHash, out (uint Index, byte[] Code) deposited) == true)
        {
            code = deposited.Code;
            return true;
        }

        code = null;
        return false;
    }
}
