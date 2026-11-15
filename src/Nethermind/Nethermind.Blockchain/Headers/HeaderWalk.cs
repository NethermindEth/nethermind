// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Headers;

/// <summary>Reads the headers that <see cref="HeaderWalk.FindHeaders{TItem, TSource}"/> walks.</summary>
/// <typeparam name="TItem">The form of a found header. Its default value marks a header that was not found.</typeparam>
public interface IHeaderWalkSource<TItem>
{
    /// <summary>Reads the header of <paramref name="hash"/> at <paramref name="number"/>.</summary>
    /// <returns><see langword="false"/> when the header is not stored.</returns>
    bool TryGet(Hash256 hash, ulong number, [MaybeNullWhen(false)] out TItem item, [MaybeNullWhen(false)] out Hash256 parentHash);
}

/// <summary>Finds the headers of a <c>GetBlockHeaders</c> request.</summary>
public static class HeaderWalk
{
    /// <summary>
    /// Finds <paramref name="numberOfBlocks"/> headers from <paramref name="hash"/> at <paramref name="number"/>, each
    /// <paramref name="skip"/> + 1 numbers after the previous one, or before it when <paramref name="reverse"/> is set.
    /// </summary>
    /// <returns>
    /// A list of <paramref name="numberOfBlocks"/> items in response order. A header that is not found is the default
    /// <typeparamref name="TItem"/>.
    /// </returns>
    /// <remarks>
    /// Without a skip, headers are found by parent hash, which needs no canonical hash lookup per number. Forward, the
    /// walk goes back from the header at the last number, and finds the headers by number when that header is not
    /// stored or does not descend from <paramref name="hash"/>. Headers found by number must descend from the previous
    /// one: by parent hash when contiguous, otherwise by the previous header still being the block its number resolves
    /// to and the next header being on the main chain.
    /// </remarks>
    public static ArrayPoolList<TItem> FindHeaders<TItem, TSource>(
        IBlockTree blockTree, ref TSource source, Hash256 hash, ulong number, int numberOfBlocks, int skip, bool reverse)
        where TSource : struct, IHeaderWalkSource<TItem>
    {
        ArrayPoolList<TItem> result = new(numberOfBlocks, numberOfBlocks);
        if (numberOfBlocks == 0 || !source.TryGet(hash, number, out TItem start, out Hash256 parentHash)) return result;

        if (skip == 0 && numberOfBlocks > 1)
        {
            Span<TItem> items = result.AsSpan();
            if (reverse)
            {
                items[0] = start;
                FillAncestors(ref source, items, 0, 1, hash, number, parentHash, out _);
                return result;
            }

            ulong endNumber = number + (ulong)(numberOfBlocks - 1);
            if (blockTree.FindHash(endNumber) is { } endHash && source.TryGet(endHash, endNumber, out TItem end, out Hash256 endParentHash))
            {
                items[^1] = end;
                int firstIndex = FillAncestors(ref source, items, items.Length - 1, -1, endHash, endNumber, endParentHash, out Hash256 firstHash);
                if (firstIndex != 0 || firstHash == hash) return result;

                // The last number resolved to a branch that the start header is not on
                items.Clear();
            }
        }

        result[0] = start;
        Hash256 previousHash = hash;
        ulong previousNumber = number;
        for (int responseIndex = 1; responseIndex < numberOfBlocks; responseIndex++)
        {
            ulong offset = (ulong)responseIndex * ((ulong)skip + 1ul);
            if (reverse && number < offset) break;

            ulong nextNumber = reverse ? number - offset : number + offset;
            if (blockTree.FindHash(nextNumber) is not { } nextHash || !source.TryGet(nextHash, nextNumber, out TItem next, out Hash256 nextParentHash)) break;

            if (!reverse && (skip == 0
                    ? nextParentHash != previousHash
                    : blockTree.FindHash(previousNumber) != previousHash || blockTree.FindCanonicalBlockInfo(nextNumber)?.BlockHash != nextHash))
            {
                break;
            }

            result[responseIndex] = next;
            previousHash = nextHash;
            previousNumber = nextNumber;
        }

        return result;
    }

    /// <summary>
    /// Puts the ancestors of the header of <paramref name="hash"/>, which is at <paramref name="index"/>, at the
    /// next indexes in the direction of <paramref name="ancestorStep"/>, until one is not found.
    /// </summary>
    /// <returns>The index of the last header put, with its hash in <paramref name="lastHash"/>.</returns>
    private static int FillAncestors<TItem, TSource>(
        ref TSource source, Span<TItem> items, int index, int ancestorStep, Hash256 hash, ulong number, Hash256 parentHash, out Hash256 lastHash)
        where TSource : struct, IHeaderWalkSource<TItem>
    {
        lastHash = hash;
        while (number > 0 && (uint)(index + ancestorStep) < (uint)items.Length
               && source.TryGet(parentHash, --number, out TItem parent, out Hash256 grandparentHash))
        {
            index += ancestorStep;
            items[index] = parent;
            lastHash = parentHash;
            parentHash = grandparentHash;
        }

        return index;
    }
}
