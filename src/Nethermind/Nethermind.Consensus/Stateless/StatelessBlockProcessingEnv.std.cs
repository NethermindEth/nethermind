// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.State;
using Nethermind.State;

namespace Nethermind.Consensus.Stateless;

public partial class StatelessBlockProcessingEnv
{
    private static partial IWorldState RequireWitnessedBytecode(WorldState worldState) => new StatelessExecutingWorldState(worldState);
}
