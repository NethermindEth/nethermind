// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Core;

/// <summary>
/// EIP-7805 separate inclusion lists: exactly <see cref="Eip7805Constants.InclusionListCommitteeSize"/> transaction
/// lists in committee-position order, an empty one where no applicable IL is available. A validator holding several
/// positions has the same list at each, and each list keeps its order and duplicates.
/// </summary>
public static class InclusionLists
{
    /// <summary>The engine API's structural checks on separate inclusion lists, or <c>null</c> when they pass.</summary>
    /// <remarks>bogota.md <c>engine_newPayloadV6</c> point 2.1: exactly one list per committee position, every entry
    /// a nonempty byte string, and no list over <see cref="Eip7805Constants.MaxBytesPerInclusionList"/> before
    /// deduplication. Transaction contents are not judged here.</remarks>
    public static string? Validate(byte[]?[]?[]? inclusionLists)
    {
        if (inclusionLists is null) return "Inclusion lists must be set";
        if (inclusionLists.Length != Eip7805Constants.InclusionListCommitteeSize)
            return $"Inclusion lists must have exactly {Eip7805Constants.InclusionListCommitteeSize} entries";

        for (int position = 0; position < inclusionLists.Length; position++)
        {
            if (inclusionLists[position] is not { } list) return $"Inclusion list at committee position {position} must be a list";

            long bytes = 0;
            foreach (byte[]? transaction in list)
            {
                if (transaction is not { Length: > 0 }) return $"Inclusion list at committee position {position} has an empty transaction";
                bytes += transaction.Length;
            }

            if (bytes > Eip7805Constants.MaxBytesPerInclusionList)
                return $"Inclusion list at committee position {position} exceeds the maximum size";
        }

        return null;
    }

    /// <summary>Each committee position's transactions, once each and in ascending hash order, the order EIP-7805's
    /// per-IL VERIFY budget fill processes them in.</summary>
    /// <remarks>A transaction's hash is the Keccak-256 of its exact envelope, so deduplicating and ordering by it is
    /// EIP-7805's <c>(keccak256(envelope), envelope)</c> order but for a hash collision.</remarks>
    public static IEnumerable<IEnumerable<Transaction>> InFillOrder(IReadOnlyList<Transaction[]> inclusionLists)
    {
        foreach (Transaction[] inclusionList in inclusionLists)
        {
            List<Transaction> ordered = new(inclusionList.Length);
            HashSet<Hash256AsKey> seen = [];
            foreach (Transaction transaction in inclusionList)
            {
                if (transaction.Hash is { } hash && seen.Add(hash)) ordered.Add(transaction);
            }
            ordered.Sort(static (a, b) => a.Hash!.CompareTo(b.Hash));
            yield return ordered;
        }
    }

    /// <summary>Every transaction across <paramref name="inclusionLists"/>, the first occurrence of each hash only,
    /// in committee-position and then list order.</summary>
    public static Transaction[] Distinct(IReadOnlyList<Transaction[]> inclusionLists)
    {
        List<Transaction> distinct = [];
        HashSet<Hash256AsKey> seen = [];
        foreach (Transaction[] inclusionList in inclusionLists)
        {
            foreach (Transaction transaction in inclusionList)
            {
                if (transaction.Hash is not { } hash || seen.Add(hash)) distinct.Add(transaction);
            }
        }
        return [.. distinct];
    }
}
