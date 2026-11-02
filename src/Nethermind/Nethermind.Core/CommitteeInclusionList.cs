// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Core;

/// <summary>
/// One counted EIP-7805 inclusion list as the Engine API carries it under EIP-8288: its transactions and the
/// <c>InclusionListProof</c> appended to the signed list.
/// </summary>
/// <remarks>EIP-8288 derives inclusion obligations per entry: a dependency-free entry always binds, and a dependency-bearing one
/// only when the list's proof verifies and its <c>proof_deps</c> cover the entry, so the lists are kept apart rather than
/// flattened.</remarks>
/// <param name="transactions">The list's decodable transactions, in order.</param>
/// <param name="starkProof">The list's <c>stark_proof</c>, empty when it carries none.</param>
/// <param name="proofDeps">The list's <c>proof_deps</c>, each a 96-byte dependency encoding, exactly as signed.</param>
public sealed class CommitteeInclusionList(Transaction[] transactions, byte[] starkProof, byte[][] proofDeps)
{
    public CommitteeInclusionList(Transaction[] transactions) : this(transactions, [], []) { }

    public Transaction[] Transactions { get; } = transactions;

    public byte[] StarkProof { get; } = starkProof;

    public byte[][] ProofDeps { get; } = proofDeps;

    /// <summary>The <c>proof_deps</c> of a nonempty proof that verified under the target block's fork, or <c>null</c> when the
    /// proof is empty or invalid, so the list obligates only its dependency-free entries.</summary>
    /// <remarks>Meaningful only once <see cref="ObligationsResolved"/>, which setting it marks.</remarks>
    public IReadOnlySet<FrameDependency>? VerifiedProofDeps
    {
        get;
        set
        {
            field = value;
            ObligationsResolved = true;
        }
    }

    /// <summary>Whether the list's proof was resolved into <see cref="VerifiedProofDeps"/>; until then a <c>null</c> there says
    /// nothing about the proof.</summary>
    public bool ObligationsResolved { get; private set; }

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
