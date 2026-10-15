// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Core;

/// <summary>EIP-8288 inclusion-list <c>recursive_stark = [stark_proof_hash, deps_hash]</c>: a commitment to a proof whose
/// dependency set, with digest <c>deps_hash</c>, contains the list's dependencies.</summary>
public readonly record struct InclusionListCommitment(ValueHash256 StarkProofHash, ValueHash256 DepsHash);

/// <summary>
/// One counted EIP-7805 inclusion list as the Engine API carries it under EIP-8288: its transactions and its signed
/// <c>recursive_stark</c> commitment, or <c>null</c> for a null commitment.
/// </summary>
/// <remarks>EIP-8288 derives inclusion obligations per list from signed data alone: a committed list binds its transactions
/// with dependencies and a null one does not, so the lists are kept apart rather than flattened.</remarks>
/// <param name="committeeIndex">The inclusion-list committee position whose list this is.</param>
/// <param name="transactions">The list's decodable transactions, in order.</param>
/// <param name="commitment">The list's <c>recursive_stark</c>, or <c>null</c> when it is null.</param>
public sealed class CommitteeInclusionList(ulong committeeIndex, Transaction[] transactions, InclusionListCommitment? commitment)
{
    public ulong CommitteeIndex { get; } = committeeIndex;

    public Transaction[] Transactions { get; } = transactions;

    /// <summary>The signed <c>recursive_stark</c>, or <c>null</c> for a null commitment.</summary>
    public InclusionListCommitment? Commitment { get; } = commitment;

    /// <summary>Whether the signed list's <c>recursive_stark</c> is non-null, obligating its dependency-bearing transactions.</summary>
    public bool IsCommitted => Commitment is not null;

    /// <summary>Concatenates the lists' transactions in list order, as EIP-7805's flattened inclusion list.</summary>
    public static Transaction[] Flatten(IReadOnlyList<CommitteeInclusionList> lists)
    {
        int count = 0;
        foreach (CommitteeInclusionList list in lists) count += list.Transactions.Length;
        Transaction[] flattened = new Transaction[count];
        int position = 0;
        foreach (CommitteeInclusionList list in lists)
        {
            list.Transactions.CopyTo(flattened, position);
            position += list.Transactions.Length;
        }
        return flattened;
    }
}
