// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
}
