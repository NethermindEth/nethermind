// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Persistence;

using System.Threading;

namespace Nethermind.State.Flat;

public interface IPersistenceManager
{
    IPersistence.IPersistenceReader LeaseReader();
    StateId GetCurrentPersistedStateId();
    Task AddToPersistence(StateId latestSnapshot);
    StateId FlushToPersistence(CancellationToken cancellationToken);
    void ResetPersistedStateId();
}
