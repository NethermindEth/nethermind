// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat;

public interface IPersistenceManager
{
    IPersistence.IPersistenceReader LeaseReader();
    StateId GetCurrentPersistedStateId();
    Task AddToPersistence(StateId latestSnapshot);
    StateId FlushToPersistence(CancellationToken cancellationToken);
    void ResetPersistedStateId();

    /// <summary>Drops every snapshot not on the ancestry of <paramref name="head"/>, serialized against
    /// persistence. Used when the head is force-reset so state kept for abandoned branches is released.</summary>
    void ResetHead(in StateId head);
}
