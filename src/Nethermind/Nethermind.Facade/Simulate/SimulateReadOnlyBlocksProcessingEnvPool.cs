// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Processing;

namespace Nethermind.Facade.Simulate;

/// <inheritdoc cref="ReadOnlyBlockProcessingEnvPool{TEnv,TScope}"/>
/// <remarks>Backs <c>eth_simulateV1</c>, whose in-flight calls each need their own processing environment.</remarks>
public sealed class SimulateReadOnlyBlocksProcessingEnvPool(
    Func<ISimulateReadOnlyBlocksProcessingEnv> factory,
    int maxConcurrent)
    : ReadOnlyBlockProcessingEnvPool<ISimulateReadOnlyBlocksProcessingEnv, SimulateReadOnlyBlocksProcessingScope>(
        factory, maxConcurrent, "simulate");
