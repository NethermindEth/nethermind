// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text;
using Nethermind.Core.Collections;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Block-level access list assembled by merging <see cref="BlockAccessListAtIndex"/> contributions
/// (one per transaction) into per-account, append-only collections. Ready for RLP encoding.
/// </summary>
public class GeneratedBlockAccessList
{
    private readonly SortedDictionary<Address, GeneratedAccountChanges> _accountChanges
        = new(GenericComparer.GetOptimized<Address>());

    public EnumerableWithCount<GeneratedAccountChanges> AccountChanges
        => new(_accountChanges.Values, _accountChanges.Values.Count);

    public bool HasAccount(Address address) => _accountChanges.ContainsKey(address);

    public GeneratedAccountChanges? GetAccountChanges(Address address)
        => _accountChanges.TryGetValue(address, out GeneratedAccountChanges? value) ? value : null;

    public int ItemCount
    {
        get
        {
            int count = _accountChanges.Count;
            foreach (GeneratedAccountChanges acc in _accountChanges.Values)
            {
                count += acc.StorageChanges.Count + acc.StorageReads.Count;
            }
            return count;
        }
    }

    /// <summary>Merge a per-tx contribution into this block-level accumulator.</summary>
    public void Merge(BlockAccessListAtIndex other)
    {
        foreach (AccountChangesAtIndex sourceAccount in other.AccountChanges)
        {
            if (!_accountChanges.TryGetValue(sourceAccount.Address, out GeneratedAccountChanges? target))
            {
                target = new GeneratedAccountChanges(sourceAccount.Address);
                _accountChanges.Add(sourceAccount.Address, target);
            }
            target.Merge(sourceAccount);
        }
    }

    public void Clear() => _accountChanges.Clear();
    public void Reset() => Clear();

    public IEnumerable<ChangeAtIndex> GetChangesAtIndex(ushort index)
    {
        foreach (GeneratedAccountChanges acc in _accountChanges.Values)
        {
            bool isSystemContract =
                acc.Address == Eip7002Constants.WithdrawalRequestPredeployAddress ||
                acc.Address == Eip7251Constants.ConsolidationRequestPredeployAddress;

            yield return new ChangeAtIndex(
                acc.Address,
                BalanceAtIndex(acc, index),
                NonceAtIndex(acc, index),
                CodeAtIndex(acc, index),
                SlotChangesAtIndex(acc, index),
                HasSlotChangesAtIndex(acc, index),
                isSystemContract ? 0 : acc.StorageReads.Count);
        }
    }

    /// <summary>For tests only — builds the accountChanges dictionary directly.</summary>
    internal static GeneratedBlockAccessList FromAccounts(IEnumerable<GeneratedAccountChanges> accounts)
    {
        GeneratedBlockAccessList bal = new();
        foreach (GeneratedAccountChanges a in accounts)
        {
            bal._accountChanges.Add(a.Address, a);
        }
        return bal;
    }

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine($"GeneratedBlockAccessList (Accounts={_accountChanges.Count})");
        foreach (GeneratedAccountChanges ac in _accountChanges.Values)
        {
            sb.Append("  ").AppendLine(ac.ToString());
        }
        return sb.ToString();
    }

    private static BalanceChange? BalanceAtIndex(GeneratedAccountChanges acc, int index)
    {
        // Linear scan — most accounts have very few entries; lists are sorted by index.
        foreach (BalanceChange b in acc.BalanceChanges)
        {
            if (b.Index == index) return b;
            if (b.Index > index) break;
        }
        return null;
    }

    private static NonceChange? NonceAtIndex(GeneratedAccountChanges acc, int index)
    {
        foreach (NonceChange n in acc.NonceChanges)
        {
            if (n.Index == index) return n;
            if (n.Index > index) break;
        }
        return null;
    }

    private static CodeChange? CodeAtIndex(GeneratedAccountChanges acc, int index)
    {
        foreach (CodeChange c in acc.CodeChanges)
        {
            if (c.Index == index) return c;
            if (c.Index > index) break;
        }
        return null;
    }

    private static IEnumerable<SlotChangeAtIndex> SlotChangesAtIndex(GeneratedAccountChanges acc, int index)
    {
        foreach (GeneratedSlotChanges slot in acc.StorageChanges)
        {
            foreach (StorageChange c in slot.Changes)
            {
                if (c.Index == index)
                {
                    yield return new SlotChangeAtIndex(slot.Key, c);
                    break;
                }
                if (c.Index > index) break;
            }
        }
    }

    private static bool HasSlotChangesAtIndex(GeneratedAccountChanges acc, int index)
    {
        foreach (GeneratedSlotChanges slot in acc.StorageChanges)
        {
            foreach (StorageChange c in slot.Changes)
            {
                if (c.Index == index) return true;
                if (c.Index > index) break;
            }
        }
        return false;
    }
}

public record struct ChangeAtIndex(
    Address Address,
    BalanceChange? BalanceChange,
    NonceChange? NonceChange,
    CodeChange? CodeChange,
    IEnumerable<SlotChangeAtIndex> SlotChanges,
    bool HasSlotChanges,
    int Reads)
{
    public override string ToString()
    {
        int slotChangeCount = 0;
        foreach (SlotChangeAtIndex _ in SlotChanges)
        {
            slotChangeCount++;
        }
        return $"{nameof(ChangeAtIndex)}({Address}, Balance={BalanceChange?.Value}, Nonce={NonceChange?.Value}, Code={CodeChange is not null}, Slots={slotChangeCount}, Reads={Reads})";
    }
}
