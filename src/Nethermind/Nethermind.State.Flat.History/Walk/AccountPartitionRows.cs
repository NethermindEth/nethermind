// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.History.Walk;

internal readonly record struct AccountRowRef(ValueHash256 Path, ulong Block, int Offset, int Length);

internal sealed class AccountPartitionRows : IDisposable
{
    private const int InitialCapacity = 1024;

    public readonly HashSet<ValueHash256> StreamedPaths = [];

    public ArrayPoolList<AccountRowRef> Start { get; private set; } = [with(InitialCapacity)];

    public ArrayPoolList<AccountRowRef> Deltas { get; private set; } = [with(InitialCapacity)];

    public RowArena Arena { get; private set; } = new();

    public long Count => Start.Count + Deltas.Count;

    public void Reset()
    {
        Dispose();
        Start = [with(InitialCapacity)];
        Deltas = [with(InitialCapacity)];
        Arena = new RowArena();
    }

    public void Dispose()
    {
        Start.Dispose();
        Deltas.Dispose();
        Arena.Dispose();
    }
}
