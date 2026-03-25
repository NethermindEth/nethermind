// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

[assembly: InternalsVisibleTo("Nethermind.Core.Test")]
[assembly: InternalsVisibleTo("Nethermind.State.Test")]
[assembly: InternalsVisibleTo("Nethermind.Evm.Test")]

namespace Nethermind.Core.BlockAccessLists;

public class BlockAccessList : IEquatable<BlockAccessList>
{
    /// storage keys across all accounts + addresses
    [JsonIgnore]
    public int ItemCount { get; set; }

    public IEnumerable<AccountChanges> AccountChanges => _accountChanges.Values;
    public bool HasAccount(Address address) => _accountChanges.ContainsKey(address);

    // todo: optimize to use hashmaps where appropriate, separate data structures for tracing and state reading
    private readonly SortedDictionary<Address, AccountChanges> _accountChanges = [];

    public BlockAccessList()
    {
    }

    public BlockAccessList(SortedDictionary<Address, AccountChanges> accountChanges)
    {
        _accountChanges = accountChanges;
    }

    public bool Equals(BlockAccessList? other) =>
        other is not null && _accountChanges.SequenceEqual(other._accountChanges);

    public override bool Equals(object? obj) =>
        obj is BlockAccessList other && Equals(other);

    public override int GetHashCode() =>
        _accountChanges.Count.GetHashCode();

    public static bool operator ==(BlockAccessList left, BlockAccessList right) =>
        left.Equals(right);

    public static bool operator !=(BlockAccessList left, BlockAccessList right) =>
        !(left == right);

    public AccountChanges? GetAccountChanges(Address address) => _accountChanges.TryGetValue(address, out AccountChanges? value) ? value : null;

    public IEnumerable<ChangeAtIndex> GetChangesAtIndex(ushort index)
    {
        foreach (AccountChanges accountChanges in AccountChanges)
        {
            bool isSystemContract =
                accountChanges.Address == Eip7002Constants.WithdrawalRequestPredeployAddress ||
                accountChanges.Address == Eip7251Constants.ConsolidationRequestPredeployAddress;

            yield return
                new(
                    accountChanges.Address,
                    accountChanges.BalanceChangeAtIndex(index),
                    accountChanges.NonceChangeAtIndex(index),
                    accountChanges.CodeChangeAtIndex(index),
                    accountChanges.SlotChangesAtIndex(index),
                    isSystemContract ? 0 : accountChanges.StorageReads.Count
                );
        }
    }

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine($"BlockAccessList (Accounts={_accountChanges.Count})");
        foreach (AccountChanges ac in _accountChanges.Values)
        {
            sb.AppendLine($"  {ac}");
        }
        return sb.ToString();
    }

    // for testing
    internal void AddAccountChanges(params AccountChanges[] accountChanges)
    {
        foreach (AccountChanges change in accountChanges)
        {
            _accountChanges.Add(change.Address, change);
        }
    }

    internal void RemoveAccountChanges(params Address[] addresses)
    {
        foreach (Address address in addresses)
        {
            _accountChanges.Remove(address);
        }
    }
}

public record struct ChangeAtIndex(Address Address, BalanceChange? BalanceChange, NonceChange? NonceChange, CodeChange? CodeChange, IEnumerable<SlotChanges> SlotChanges, int Reads)
{
    public override string ToString()
    {
        int slotChangeCount = 0;
        foreach (SlotChanges _ in SlotChanges)
        {
            slotChangeCount++;
        }

        return $"{nameof(ChangeAtIndex)}({Address}, Balance={BalanceChange?.PostBalance}, Nonce={NonceChange?.NewNonce}, Code={CodeChange is not null}, Slots={slotChangeCount}, Reads={Reads})";
    }
}
