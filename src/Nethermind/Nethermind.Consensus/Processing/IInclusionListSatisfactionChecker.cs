// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.State;

namespace Nethermind.Consensus.Processing;

/// <summary>Reports whether a processed block honoured the inclusion list it was suggested with (EIP-7805).</summary>
/// <remarks>
/// Deliberately not part of block validation: an unsatisfied inclusion list leaves the block valid and
/// committed, and the outcome only reaches the consensus layer as an engine-API status. The check reads
/// post-execution world state, so it runs inside block processing.
/// </remarks>
public interface IInclusionListSatisfactionChecker
{
    /// <param name="processedBlock">The block as produced by execution.</param>
    /// <param name="suggestedBlock">The block as suggested, carrying the inclusion list to satisfy.</param>
    /// <param name="worldState">Post-execution state, used to test whether a missing entry was appendable.</param>
    bool IsSatisfied(Block processedBlock, Block suggestedBlock, IWorldState worldState, ProcessingOptions options);
}
