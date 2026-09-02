// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.History.Walk;

internal readonly record struct AccountRowRef(ValueHash256 Path, ulong Block, int Offset, int Length);

internal sealed class AccountPartitionRows
{
    public readonly HashSet<ValueHash256> StreamedPaths = [];

    public List<AccountRowRef> Start { get; private set; } = [];

    public List<AccountRowRef> Deltas { get; private set; } = [];

    public RowArena Arena { get; private set; } = new();

    public long Count => Start.Count + Deltas.Count;

    public void Reset()
    {
        Start = [];
        Deltas = [];
        Arena = new RowArena();
    }
}
