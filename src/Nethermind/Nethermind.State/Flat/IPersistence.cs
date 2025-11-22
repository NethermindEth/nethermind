// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat;

public interface IPersistence
{
    IPersistenceReader CreateReader();
    void Add(Snapshot snapshot);
    StateId CurrentState { get; }
}
