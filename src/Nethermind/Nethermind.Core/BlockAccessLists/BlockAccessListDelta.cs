// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>The combined effect on state of consecutive block access lists, built from <see cref="BalAccountDelta"/>.</summary>
/// <remarks>Adding lists one by one gives the same state as applying each list in turn.</remarks>
public sealed class BlockAccessListDelta
{
    private readonly Dictionary<AddressAsKey, AccountDelta> _accounts = [];
    private Dictionary<ValueHash256, byte[]>? _depositedCode;

    /// <summary>The accounts the added lists changed.</summary>
    public IReadOnlyDictionary<AddressAsKey, AccountDelta> Accounts => _accounts;

    /// <summary>Adds the next list in order.</summary>
    /// <param name="bal">The list.</param>
    /// <param name="getAccount">Reads an account's state from before the first list.</param>
    public void Add(ReadOnlyBlockAccessList bal, Func<Address, Account?> getAccount)
    {
        foreach (ReadOnlyAccountChanges changes in bal.AccountChanges)
        {
            if (!changes.HasStateChanges) continue;

            ref AccountDelta? delta = ref CollectionsMarshal.GetValueRefOrAddDefault(_accounts, changes.Address, out _);
            delta ??= new AccountDelta(getAccount(changes.Address));
            delta.Apply(BalAccountDelta.Compute(delta.Account, changes));

            foreach (CodeChange codeChange in changes.CodeChanges)
            {
                if (!codeChange.IsAdopted) (_depositedCode ??= []).TryAdd(codeChange.CodeHash, codeChange.Code);
            }
        }
    }

    /// <summary>Gets the bytecode for <paramref name="change"/> from the change itself or, when adopted, from any added list.</summary>
    /// <returns>
    /// <see langword="false"/> for an EIP-8298 adoption of code no added list deposited: its bytecode should already be
    /// in the code database.
    /// </returns>
    public bool TryGetCode(in CodeChange change, [NotNullWhen(true)] out byte[]? code)
    {
        if (!change.IsAdopted)
        {
            code = change.Code;
            return true;
        }

        code = null;
        return _depositedCode?.TryGetValue(change.CodeHash, out code) == true;
    }

    /// <summary>What the added lists did to one account.</summary>
    public sealed class AccountDelta
    {
        internal AccountDelta(Account? account) => Account = account;

        /// <summary>The account after the last list, or <see langword="null"/> when it does not exist afterwards.</summary>
        public Account? Account { get; private set; }

        /// <summary>Whether a list removed the account, dropping its storage from before that point.</summary>
        public bool WipesStorage { get; private set; }

        /// <summary>The change that set the account's final code, or <see langword="null"/> when its code is unchanged.</summary>
        public CodeChange? Code { get; private set; }

        /// <summary>The slots written since the last removal, with their final values.</summary>
        public IReadOnlyDictionary<UInt256, UInt256>? Slots => _slots;

        private Dictionary<UInt256, UInt256>? _slots;

        internal void Apply(in BalAccountDelta delta)
        {
            if (delta.WipesStorage)
            {
                WipesStorage = true;
                _slots?.Clear();
                Code = delta.Code;
            }
            else if (delta.Code is not null)
            {
                Code = delta.Code;
            }

            Account = delta.Account;
            foreach (ReadOnlySlotChanges slot in delta.Changes.StorageChanges)
            {
                if (delta.TryGetFinalValue(slot, out UInt256 value)) (_slots ??= [])[slot.Key] = value;
            }
        }
    }
}
