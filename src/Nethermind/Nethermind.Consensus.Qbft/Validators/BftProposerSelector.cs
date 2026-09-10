// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Core;

namespace Nethermind.Consensus.Qbft.Validators;

public interface IProposerSelector
{
    /// <exception cref="InvalidOperationException">The parent of the round's height is unknown.</exception>
    Address SelectProposerForRound(ConsensusRoundIdentifier roundIdentifier);
}

/// <summary>
/// Round-robin proposer selection: the validator after the previous block's proposer (in address
/// order) proposes round 0, each further round advances one more validator.
/// </summary>
/// <remarks>
/// When the previous proposer is no longer a validator the walk starts from the next higher
/// address (wrapping to the first). QBFT always rotates the proposer every block.
/// </remarks>
public sealed class BftProposerSelector(IBlockTree blockTree, IValidatorProvider validatorProvider) : IProposerSelector
{
    public Address SelectProposerForRound(ConsensusRoundIdentifier roundIdentifier)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(roundIdentifier.Round);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(roundIdentifier.Sequence);

        ulong parentNumber = (ulong)roundIdentifier.Sequence - 1;
        BlockHeader parent = blockTree.FindHeader(parentNumber, BlockTreeLookupOptions.RequireCanonical)
                             ?? throw new InvalidOperationException($"Unable to determine past proposer, block {parentNumber} is unknown.");

        return SelectProposerForRound(roundIdentifier, QbftBlockInterface.GetProposer(parent), validatorProvider.GetValidatorsAfterBlock(parent));
    }

    public static Address SelectProposerForRound(ConsensusRoundIdentifier roundIdentifier, Address previousProposer, IReadOnlyList<Address> validators)
    {
        if (validators.Count == 0)
        {
            throw new InvalidOperationException("Cannot select a proposer from an empty validator set.");
        }

        Address[] sorted = [.. validators];
        Array.Sort(sorted);
        int previousIndex = Array.IndexOf(sorted, previousProposer);
        int offset;
        if (previousIndex < 0)
        {
            // The previous proposer was dropped: start from the first validator above it, wrapping around.
            previousIndex = 0;
            while (previousIndex < sorted.Length && sorted[previousIndex].CompareTo(previousProposer) < 0)
            {
                previousIndex++;
            }

            previousIndex %= sorted.Length;
            offset = roundIdentifier.Round;
        }
        else
        {
            offset = roundIdentifier.Round + 1;
        }

        return sorted[(int)(((long)previousIndex + offset) % sorted.Length)];
    }
}
