// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat;

public class RocksdbPersistence: IPersistence
{
    public IPersistenceReader CreateReader()
    {
        throw new System.NotImplementedException();
    }

    public void Add(Snapshot snapshot)
    {
        throw new System.NotImplementedException();
    }

    public StateId CurrentState { get; }
}
