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
    /// <remarks>Looks up the block numbers in one batch.</remarks>
    /// <returns>The number of known blocks.</returns>
    public static int GetKnownBlocks(this IHeaderStore headerStore, IReadOnlyList<Hash256> hashes, int start, int count, Span<(ulong Number, ValueHash256 Hash)> blocks)
    {
        using ArrayPoolSpan<ValueHash256> blockHashes = new(count);
        for (int i = 0; i < count; i++) blockHashes[i] = hashes[start + i].ValueHash256;

        using ArrayPoolSpan<ulong?> numbers = new(count);
        headerStore.GetBlockNumbers(blockHashes, numbers);

        int known = 0;
        for (int i = 0; i < count; i++)
        {
            if (numbers[i] is { } number) blocks[known++] = (number, blockHashes[i]);
        }

        return known;
    }
}
