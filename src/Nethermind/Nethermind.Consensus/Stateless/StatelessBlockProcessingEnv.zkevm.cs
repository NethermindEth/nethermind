// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Stateless;

public partial class StatelessBlockProcessingEnv
{
    /// <remarks>
    /// The guest executes on <see cref="GuestWorldState"/>: it checks bytecode access itself and resolves each state
    /// access with far fewer lookups than <see cref="WorldState"/>.
    /// </remarks>
    private static partial IWorldState CreateWorldState(IWorldStateScopeProvider scopeProvider, ILogManager logManager) =>
        new GuestWorldState(scopeProvider);

    /// <remarks>A plain, unbounded map rather than <see cref="StaticCodeCache"/>: see <see cref="GuestCodeCache"/>.</remarks>
    private static partial ICodeCache CreateCodeCache() => new GuestCodeCache(CodeCacheCapacity);
}
