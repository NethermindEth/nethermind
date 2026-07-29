// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.Processing.BlockLevelAccessList;

/// <summary>
/// A <see cref="IBalEnvManager"/> backed by a bounded pool of parallel workers.
/// </summary>
public interface IParallelBalEnvManager : IBalEnvManager
{
    /// <summary>
    /// Detaches the worker's populated BAL into the per-tx slot and recycles the processor
    /// immediately, so workers never block on the validator.
    /// </summary>
    void Return(uint balIndex);
}
