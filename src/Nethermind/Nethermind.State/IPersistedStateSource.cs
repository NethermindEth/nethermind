// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.State;

/// <summary>
/// Exposes the newest block whose post-state is fully persisted by the state backend. After an unclean
/// shutdown this can be ahead of the block tree head; when the backend cannot roll state back, the gap
/// blocks must be fast-forwarded over instead of re-executed.
/// </summary>
public interface IPersistedStateSource
{
    bool TryGetPersistedState(out ulong blockNumber, out Hash256 stateRoot);
}
