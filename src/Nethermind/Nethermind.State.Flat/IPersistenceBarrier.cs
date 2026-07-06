// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat;

/// <summary>
/// Invoked before the persisted flat state pointer advances, so implementations can make what it
/// depends on durable first — the DBs are separate RocksDB instances with no cross-DB ordering,
/// and an unclean shutdown must not leave persisted state ahead of the durable block tree.
/// </summary>
public interface IPersistenceBarrier
{
    void BeforePersistedStateAdvance();
}
