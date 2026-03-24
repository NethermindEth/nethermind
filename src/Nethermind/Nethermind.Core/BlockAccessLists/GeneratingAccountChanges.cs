
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

public class GeneratingAccountChanges 
{
    public IList<SlotChanges> StorageChanges => _storageChanges.Values;
    public BalanceChange? BalanceChange { get; private set; }
    public NonceChange? NonceChange { get; private set; }
    public CodeChange? CodeChange { get; private set; }
    private readonly SortedList<UInt256, SlotChanges> _storageChanges;
    private readonly SortedSet<StorageRead> _storageReads;

    public GeneratingAccountChanges()
    {
        _storageChanges = [];
        _storageReads = [];
    }

    // n.b. implies that length of changes is zero
    public bool HasStorageChange(UInt256 key)
        => _storageChanges.ContainsKey(key);

    public bool TryGetSlotChanges(UInt256 key, [NotNullWhen(true)] out SlotChanges? slotChanges)
        => _storageChanges.TryGetValue(key, out slotChanges);

    public void ClearEmptySlotChangesAndAddRead(UInt256 key)
    {
        if (TryGetSlotChanges(key, out SlotChanges? slotChanges) && slotChanges.Changes.Count == 0)
        {
            _storageChanges.Remove(key);
            _storageReads.Add(new(key));
        }
    }

    public SlotChanges GetOrAddSlotChanges(UInt256 key)
    {
        if (!_storageChanges.TryGetValue(key, out SlotChanges? existing))
        {
            SlotChanges slotChanges = new(key);
            _storageChanges.Add(key, slotChanges);
            return slotChanges;
        }
        return existing;
    }

    public void AddStorageRead(UInt256 key)
        => _storageReads.Add(new(key));

    public void RemoveStorageRead(UInt256 key)
        => _storageReads.Remove(new(key));

    public void SelfDestruct()
    {
        foreach (UInt256 key in _storageChanges.Keys)
        {
            AddStorageRead(key);
        }

        _storageChanges.Clear();
        NonceChange = null;
        CodeChange = null;
    }

    public void AddBalanceChange(BalanceChange balanceChange)
        => BalanceChange = balanceChange;

    public void PopBalanceChange(out BalanceChange? balanceChange)
    {
        balanceChange = BalanceChange;
        BalanceChange = null;
    }

    public void AddNonceChange(NonceChange nonceChange)
        => NonceChange = nonceChange;

    public void PopNonceChange(out NonceChange? nonceChange)
    {
        nonceChange = NonceChange;
        NonceChange = null;
    }

    public void AddCodeChange(CodeChange codeChange)
        => CodeChange = codeChange;

    public void PopCodeChange(out CodeChange? codeChange)
    {
        codeChange = CodeChange;
        CodeChange = null;
    }
}
