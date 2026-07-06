// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat;

public sealed class NullPersistenceBarrier : IPersistenceBarrier
{
    public static readonly NullPersistenceBarrier Instance = new();

    private NullPersistenceBarrier() { }

    public void BeforePersistedStateAdvance() { }
}
