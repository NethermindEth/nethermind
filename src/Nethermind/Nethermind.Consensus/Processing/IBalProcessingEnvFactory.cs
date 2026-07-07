// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Builds <see cref="IBalProcessingEnv"/> workers bound to the block-processing state. One factory
/// backs both pools; the <c>parallel</c> flag selects BAL-backed vs plain world state.
/// </summary>
internal interface IBalProcessingEnvFactory
{
    IBalProcessingEnv Create(bool parallel);
}
