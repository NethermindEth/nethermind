// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat;

/// <summary>Reads the accounts of the persisted flat state.</summary>
public interface IPivotStateSource
{
    /// <summary>The state the flat columns hold.</summary>
    StateId PersistedState { get; }

    /// <summary>Calls <paramref name="sink"/> once per account of the state at <paramref name="stateRoot"/> whose
    /// hashed address starts with the byte <paramref name="partition"/>, on the calling thread.</summary>
    void ForEachAccount(in ValueHash256 stateRoot, byte partition, Action<ValueHash256, Account> sink, CancellationToken cancellationToken);
}
