// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.Processing;

/// <summary>Decides the state-dependent half of EIP-8369 Profile 2 for an omitted inclusion-list entry.</summary>
public interface IProfile2EligibilityReplayer
{
    /// <summary>Whether <paramref name="transaction"/>'s protocol-validated frame signatures verify, as the
    /// EIP-8369 budget fill needs before it debits the validation prefix cost.</summary>
    bool AreSignaturesValid(Transaction transaction, IReleaseSpec spec);

    /// <summary>Whether a Profile 2 candidate is eligible when inserted at <paramref name="index"/> of
    /// <paramref name="block"/>.</summary>
    /// <remarks>
    /// Returns <c>false</c> only when the replay proves the candidate ineligible. A replay this node cannot
    /// run answers <c>true</c>: EIP-8369 makes local data availability no omission excuse.
    /// </remarks>
    /// <param name="index">Transactions of <paramref name="block"/> that precede the insertion point, in
    /// <c>[0, len(block.transactions)]</c>.</param>
    bool IsEligible(Block block, Transaction transaction, int index, IReleaseSpec spec);
}
