// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing;

internal sealed class BlockAccessListValidationIndex
{
    private readonly AddressIndex _addressIndex;
    private readonly IndexDelta[] _indices;
    private readonly HashSet<int> _accounts = [];
    private readonly uint _lastIndex;

    public BlockAccessListValidationIndex(int txCount, AddressIndex addressIndex)
    {
        _addressIndex = addressIndex;
        _lastIndex = checked((uint)txCount + 1);
        _indices = new IndexDelta[_lastIndex + 1];
        for (int i = 0; i < _indices.Length; i++)
        {
            _indices[i] = new();
        }
    }

    public static BlockAccessListValidationIndex Build(BlockAccessList blockAccessList, int txCount, AddressIndex addressIndex)
    {
        BlockAccessListValidationIndex index = new(txCount, addressIndex);
        index.Add(blockAccessList, trackAccounts: true);
        return index;
    }

    public void Add(BlockAccessList blockAccessList) => Add(blockAccessList, trackAccounts: false);

    public bool HasAccount(Address address) =>
        _addressIndex.TryGet(address, out int accountOrdinal) && _accounts.Contains(accountOrdinal);

    private void Add(BlockAccessList blockAccessList, bool trackAccounts)
    {
        foreach (AccountChanges accountChanges in blockAccessList.UnorderedAccountChanges)
        {
            int accountOrdinal = _addressIndex.GetOrAdd(accountChanges.Address);
            if (trackAccounts)
            {
                _accounts.Add(accountOrdinal);
            }

            ReadOnlySpan<BalanceChange> balanceChanges = accountChanges.BalanceChangeSet.BlockAccessChanges;
            for (int i = 0; i < balanceChanges.Length; i++)
            {
                AddBalanceChange(accountOrdinal, balanceChanges[i]);
            }

            ReadOnlySpan<NonceChange> nonceChanges = accountChanges.NonceChangeSet.BlockAccessChanges;
            for (int i = 0; i < nonceChanges.Length; i++)
            {
                AddNonceChange(accountOrdinal, nonceChanges[i]);
            }

            ReadOnlySpan<CodeChange> codeChanges = accountChanges.CodeChangeSet.BlockAccessChanges;
            for (int i = 0; i < codeChanges.Length; i++)
            {
                AddCodeChange(accountOrdinal, codeChanges[i]);
            }

            foreach (SlotChanges slotChanges in accountChanges.StorageChanges)
            {
                ReadOnlySpan<StorageChange> storageChanges = slotChanges.Changes.BlockAccessChanges;
                for (int i = 0; i < storageChanges.Length; i++)
                {
                    AddStorageChange(accountOrdinal, slotChanges.Key, storageChanges[i]);
                }
            }
        }
    }

    public bool ChangesEqual(BlockAccessListValidationIndex other, uint index)
    {
        if (index > _lastIndex || index > other._lastIndex)
        {
            return false;
        }

        return _indices[index].ChangesEqual(other._indices[index]);
    }

    private void AddBalanceChange(int accountOrdinal, BalanceChange balanceChange)
    {
        if (!TryGetIndex(balanceChange.Index, out IndexDelta? index))
        {
            return;
        }

        index.GetOrAddAccount(accountOrdinal).BalanceChange = balanceChange;
        index.BalanceChangesCount++;
    }

    private void AddNonceChange(int accountOrdinal, NonceChange nonceChange)
    {
        if (!TryGetIndex(nonceChange.Index, out IndexDelta? index))
        {
            return;
        }

        index.GetOrAddAccount(accountOrdinal).NonceChange = nonceChange;
        index.NonceChangesCount++;
    }

    private void AddCodeChange(int accountOrdinal, CodeChange codeChange)
    {
        if (!TryGetIndex(codeChange.Index, out IndexDelta? index))
        {
            return;
        }

        index.GetOrAddAccount(accountOrdinal).CodeChange = codeChange;
        index.CodeChangesCount++;
    }

    private void AddStorageChange(int accountOrdinal, UInt256 key, StorageChange storageChange)
    {
        if (!TryGetIndex(storageChange.Index, out IndexDelta? index))
        {
            return;
        }

        index.GetOrAddAccount(accountOrdinal).AddStorageChange(key, storageChange);
        index.StorageChangesCount++;
    }

    private bool TryGetIndex(uint index, out IndexDelta? delta)
    {
        if (index > _lastIndex)
        {
            delta = null;
            return false;
        }

        delta = _indices[index];
        return true;
    }

    internal sealed class AddressIndex
    {
        private readonly Dictionary<AddressAsKey, int> _ordinals = new(AddressAsKey.EqualityComparer);

        public int GetOrAdd(Address address)
        {
            AddressAsKey key = address;
            if (_ordinals.TryGetValue(key, out int ordinal))
            {
                return ordinal;
            }

            ordinal = _ordinals.Count;
            _ordinals.Add(key, ordinal);
            return ordinal;
        }

        public bool TryGet(Address address, out int ordinal)
        {
            AddressAsKey key = address;
            return _ordinals.TryGetValue(key, out ordinal);
        }
    }

    private sealed class IndexDelta
    {
        private Dictionary<int, AccountDelta>? _accounts;

        public int BalanceChangesCount { get; set; }
        public int NonceChangesCount { get; set; }
        public int CodeChangesCount { get; set; }
        public int StorageChangesCount { get; set; }

        public AccountDelta GetOrAddAccount(int ordinal)
        {
            _accounts ??= [];
            if (_accounts.TryGetValue(ordinal, out AccountDelta? account))
            {
                return account;
            }

            account = new();
            _accounts.Add(ordinal, account);
            return account;
        }

        public bool ChangesEqual(IndexDelta other)
        {
            if (BalanceChangesCount != other.BalanceChangesCount ||
                NonceChangesCount != other.NonceChangesCount ||
                CodeChangesCount != other.CodeChangesCount ||
                StorageChangesCount != other.StorageChangesCount)
            {
                return false;
            }

            if (_accounts is null)
            {
                return other._accounts is null;
            }

            foreach (KeyValuePair<int, AccountDelta> pair in _accounts)
            {
                if (other._accounts is null ||
                    !other._accounts.TryGetValue(pair.Key, out AccountDelta? otherAccount) ||
                    !pair.Value.ChangesEqual(otherAccount))
                {
                    return false;
                }
            }

            return true;
        }
    }

    private sealed class AccountDelta
    {
        private Dictionary<UInt256, StorageChange>? _storageChanges;

        public BalanceChange? BalanceChange { get; set; }
        public NonceChange? NonceChange { get; set; }
        public CodeChange? CodeChange { get; set; }

        public void AddStorageChange(UInt256 key, StorageChange storageChange)
        {
            _storageChanges ??= new(GenericEqualityComparer.GetOptimized<UInt256>());
            _storageChanges.Add(key, storageChange);
        }

        public bool ChangesEqual(AccountDelta other)
        {
            if (BalanceChange != other.BalanceChange ||
                NonceChange != other.NonceChange ||
                CodeChange.HasValue != other.CodeChange.HasValue ||
                CodeChange is not null && !CodeChange.Value.Equals(other.CodeChange.Value))
            {
                return false;
            }

            if (_storageChanges is null)
            {
                return other._storageChanges is null;
            }

            if (other._storageChanges is null || _storageChanges.Count != other._storageChanges.Count)
            {
                return false;
            }

            foreach (KeyValuePair<UInt256, StorageChange> pair in _storageChanges)
            {
                if (!other._storageChanges.TryGetValue(pair.Key, out StorageChange otherStorageChange) ||
                    !pair.Value.Equals(otherStorageChange))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
