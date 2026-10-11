// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Core;

/// <summary>
/// One counted EIP-7805 inclusion list in the EIP-8288 FOCIL form <c>[transactions, recursive_stark]</c>, with the committee
/// position that authenticated it.
/// </summary>
/// <remarks>EIP-8288 derives inclusion obligations per list: its transactions with dependencies impose one only if
/// <see cref="Proof"/> is valid, so the lists are kept apart rather than flattened.</remarks>
/// <param name="committeeIndex">The inclusion-list committee position whose list this is.</param>
/// <param name="transactions">The list's decodable transactions, in order.</param>
/// <param name="proof">The list's <c>recursive_stark</c>, or <c>null</c> when it carries none.</param>
public sealed class CommitteeInclusionList(ulong committeeIndex, Transaction[] transactions, RecursiveStark? proof)
{
    public ulong CommitteeIndex { get; } = committeeIndex;

    public Transaction[] Transactions { get; } = transactions;

    public RecursiveStark? Proof { get; } = proof;

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
