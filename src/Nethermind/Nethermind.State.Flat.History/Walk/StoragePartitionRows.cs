// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.History.Walk;

internal readonly record struct StorageRowRef(int Contract, ValueHash256 Slot, ulong Block, int Offset, int Length);

internal readonly record struct ClearRecord(ValueHash256 Identity, ulong Block);

internal sealed class StoragePartitionRows : IDisposable
{
    private const int InitialCapacity = 1024;

    private readonly Dictionary<ValueHash256, int> _contracts = [];

    public readonly List<ValueHash256> Identities = [];
    public readonly HashSet<(int Contract, ValueHash256 Slot)> StreamedSlots = [];

    public ArrayPoolList<StorageRowRef> Start { get; private set; } = new(InitialCapacity);

    public ArrayPoolList<StorageRowRef> Deltas { get; private set; } = new(InitialCapacity);

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

    public bool TryGetContract(in ValueHash256 identity, out int index) => _contracts.TryGetValue(identity, out index);

    public void Reset()
    {
        Dispose();
        Start = new ArrayPoolList<StorageRowRef>(InitialCapacity);
        Deltas = new ArrayPoolList<StorageRowRef>(InitialCapacity);
        Arena = new RowArena();
    }

    public void Dispose()
    {
        Start.Dispose();
        Deltas.Dispose();
        Arena.Dispose();
    }
}
