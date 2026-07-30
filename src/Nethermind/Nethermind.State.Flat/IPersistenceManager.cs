// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat;

public interface IPersistenceManager
{
    IPersistence.IPersistenceReader LeaseReader();

    /// <inheritdoc cref="LeaseReader()"/>
    /// <param name="flags">Forwarded to <see cref="IPersistence.CreateReader"/>; implementations may ignore it.</param>
    IPersistence.IPersistenceReader LeaseReader(ReaderFlags flags) => LeaseReader();
    StateId GetCurrentPersistedStateId();
    Task AddToPersistence(StateId latestSnapshot);
    StateId FlushToPersistence();
    void ResetPersistedStateId();
}
