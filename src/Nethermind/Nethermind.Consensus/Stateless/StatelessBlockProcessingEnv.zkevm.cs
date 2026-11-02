// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.State;

namespace Nethermind.Consensus.Stateless;

public partial class StatelessBlockProcessingEnv
{
    /// <remarks>
    /// The guest's <see cref="WorldState"/> checks bytecode access itself, so no decorator puts a second interface
    /// call in front of every state access.
    /// </remarks>
    private static partial IWorldState RequireWitnessedBytecode(WorldState worldState) => worldState;

    /// <remarks>A plain, unbounded map rather than <see cref="StaticCodeCache"/>: see <see cref="GuestCodeCache"/>.</remarks>
    private static partial ICodeCache CreateCodeCache() => new GuestCodeCache(CodeCacheCapacity);
}
