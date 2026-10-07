// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.Processing;

/// <summary>Decides the state-dependent half of EIP-8369 Profile 2 for an omitted inclusion-list entry.</summary>
public interface IProfile2EligibilityReplayer
{
    /// <summary>Whether <paramref name="transaction"/>'s protocol-validated frame signatures verify, as the
    /// EIP-8369 budget fill needs before it debits the validation prefix cost.</summary>
    bool AreSignaturesValid(Transaction transaction, IReleaseSpec spec);

    /// <summary>Whether each Profile 2 candidate is eligible when inserted at its index of <paramref name="block"/>.</summary>
    /// <remarks>
    /// A verdict is <c>false</c> only when the replay proves the candidate ineligible. A replay this node cannot
    /// run answers <c>true</c>: EIP-8369 makes local data availability no omission excuse. The block's state is
    /// reconstructed once for the whole batch, so callers should pass every request for a block together.
    /// </remarks>
    /// <param name="requests">Each candidate with the number of <paramref name="block"/>'s transactions that
    /// precede its insertion point, in <c>[0, len(block.transactions)]</c>.</param>
    /// <returns>One verdict per request, in order.</returns>
    bool[] AreEligible(Block block, IReadOnlyList<(Transaction Transaction, int Index)> requests, IReleaseSpec spec);
}
