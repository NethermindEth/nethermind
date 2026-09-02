// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.History.Walk;

internal readonly record struct AccountRowRef(ValueHash256 Path, ulong Block, int Offset, int Length);

internal readonly record struct StorageRowRef(int Contract, ValueHash256 Slot, ulong Block, int Offset, int Length);

internal readonly record struct ClearRecord(ValueHash256 Identity, ulong Block);

internal sealed class AccountPartitionRows
{
    public readonly List<AccountRowRef> Start = [];
    public readonly List<AccountRowRef> Deltas = [];
    public readonly HashSet<ValueHash256> StreamedPaths = [];

    public RowArena Arena { get; private set; } = new();

    public long Count => Start.Count + Deltas.Count;

    public void Reset()
    {
        Start.Clear();
        Deltas.Clear();
        Arena = new RowArena();
    }
}

internal sealed class StoragePartitionRows
{
    private readonly Dictionary<ValueHash256, int> _contracts = [];

    public readonly List<ValueHash256> Identities = [];
    public readonly List<StorageRowRef> Start = [];
    public readonly List<StorageRowRef> Deltas = [];
    public readonly HashSet<(int Contract, ValueHash256 Slot)> StreamedSlots = [];

    public RowArena Arena { get; private set; } = new();

    public long Count => Start.Count + Deltas.Count;

    public int ContractOf(in ValueHash256 identity)
    {
        if (_contracts.TryGetValue(identity, out int index)) return index;

        index = Identities.Count;
        Identities.Add(identity);
        _contracts[identity] = index;
        return index;
    }

    public void Reset()
    {
        Start.Clear();
        Deltas.Clear();
        Arena = new RowArena();
    }
}
