// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Core;

/// <summary>
/// EIP-7805 inclusion-list membership: for each entry of the deduplicated list, an SSZ
/// <c>Bitvector[INCLUSION_LIST_COMMITTEE_SIZE]</c> whose bit <c>i</c> is set when committee position <c>i</c>'s list
/// carries it. It restores the per-includer provenance the EIP-8369 VERIFY budget is filled over.
/// </summary>
/// <remarks>A mask is a <see cref="ushort"/>, which holds the <see cref="Eip7805Constants.InclusionListCommitteeSize"/> of 16.</remarks>
public static class InclusionListMembership
{
    /// <summary>The bytes of one membership entry.</summary>
    public const int BytesPerEntry = Eip7805Constants.InclusionListCommitteeSize / 8;

    /// <summary>The committee positions a well-formed entry sets, bit <c>i</c> being position <c>i</c>.</summary>
    /// <remarks>SSZ packs a bitvector little-endian, within each byte and across them.</remarks>
    public static ushort ToMask(ReadOnlySpan<byte> entry) => BinaryPrimitives.ReadUInt16LittleEndian(entry);

    /// <summary>The engine API's structural checks on a membership beside its transactions, or <c>null</c> when it passes.</summary>
    /// <remarks>bogota.md <c>engine_newPayloadV6</c> point 2: one entry per transaction, each exactly
    /// <see cref="BytesPerEntry"/> bytes with a bit set, and no position's transactions over
    /// <see cref="Eip7805Constants.MaxBytesPerInclusionList"/>.</remarks>
    public static string? Validate(byte[][] transactions, byte[]?[] membership)
    {
        if (membership.Length != transactions.Length)
            return "Inclusion list membership does not have one entry per inclusion list transaction";

        Span<long> bytesAtPosition = stackalloc long[Eip7805Constants.InclusionListCommitteeSize];
        for (int i = 0; i < membership.Length; i++)
        {
            if (membership[i] is not { Length: BytesPerEntry } entry)
                return $"Inclusion list membership entry must be exactly {BytesPerEntry} bytes";

            ushort mask = ToMask(entry);
            if (mask == 0) return "Inclusion list membership entry has no bit set";

            int length = transactions[i]?.Length ?? 0;
            for (int position = 0; position < bytesAtPosition.Length; position++)
            {
                if ((mask & (1 << position)) != 0) bytesAtPosition[position] += length;
            }
        }

        for (int position = 0; position < bytesAtPosition.Length; position++)
        {
            if (bytesAtPosition[position] > Eip7805Constants.MaxBytesPerInclusionList)
                return $"Inclusion list at committee position {position} exceeds the maximum size";
        }
        return null;
    }

    /// <summary>Each committee position's transactions, once each and in ascending hash order, the order
    /// EIP-8369's per-IL budget fill processes them in.</summary>
    /// <param name="transactions">The decoded list.</param>
    /// <param name="masks">One mask per entry of <paramref name="transactions"/>.</param>
    public static IEnumerable<IEnumerable<Transaction>> ByPosition(Transaction[] transactions, ushort[] masks)
    {
        for (int position = 0; position < Eip7805Constants.InclusionListCommitteeSize; position++)
        {
            List<Transaction> list = [];
            HashSet<Hash256AsKey> seen = [];
            for (int i = 0; i < transactions.Length; i++)
            {
                if ((masks[i] & (1 << position)) != 0 && transactions[i].Hash is { } hash && seen.Add(hash))
                    list.Add(transactions[i]);
            }
            list.Sort(static (a, b) => a.Hash!.CompareTo(b.Hash));
            yield return list;
        }
    }
}
