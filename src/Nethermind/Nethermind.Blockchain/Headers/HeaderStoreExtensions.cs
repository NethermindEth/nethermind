// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Headers;

public static class HeaderStoreExtensions
{
    /// <summary>Puts the number and hash of each known block of <paramref name="count"/> hashes from <paramref name="start"/> in <paramref name="blocks"/>, in order.</summary>
    /// <remarks>Looks up the block numbers in one batch. A null hash, which an engine API request can hold, is not known.</remarks>
    /// <returns>The number of known blocks.</returns>
    public static int GetKnownBlocks(this IHeaderStore headerStore, IReadOnlyList<Hash256?> hashes, int start, int count, Span<(ulong Number, ValueHash256 Hash)> blocks)
    {
        using ArrayPoolSpan<ValueHash256> blockHashes = new(count);
        int requested = 0;
        for (int i = 0; i < count; i++)
        {
            if (hashes[start + i] is { } hash) blockHashes[requested++] = hash.ValueHash256;
        }

        using ArrayPoolSpan<ulong?> numbers = new(requested);
        headerStore.GetBlockNumbers(blockHashes.Slice(0, requested), numbers);

        int known = 0;
        for (int i = 0; i < requested; i++)
        {
            if (numbers[i] is { } number) blocks[known++] = (number, blockHashes[i]);
        }

        return known;
    }
}
