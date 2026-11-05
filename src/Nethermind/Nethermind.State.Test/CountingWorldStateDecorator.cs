// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Store.Test;

/// <summary>Counts the storage calls that reach the decorated state.</summary>
internal sealed class CountingWorldStateDecorator(IWorldState state) : WorldStateDecorator(state)
{
    public int Reads { get; private set; }
    public int Writes { get; private set; }
    public int TransientWrites { get; private set; }

    public override void Get(in StorageCell cell, out UInt256 value)
    {
        Reads++;
        base.Get(in cell, out value);
    }

    public override void Set(in StorageCell cell, in UInt256 value)
    {
        Writes++;
        base.Set(in cell, in value);
    }

    public override void SetTransientState(in StorageCell cell, in UInt256 value)
    {
        TransientWrites++;
        base.SetTransientState(in cell, in value);
    }
}
