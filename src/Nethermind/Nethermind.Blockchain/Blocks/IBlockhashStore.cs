// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.Blockchain.Blocks;

public interface IBlockhashStore
{
    public void ApplyBlockhashStateChanges(BlockHeader blockHeader, IReleaseSpec spec);
    public Hash256? GetBlockHashFromState(BlockHeader currentBlockHeader, ulong requiredBlockNumber, IReleaseSpec spec);

    /// <inheritdoc cref="GetBlockHashFromState"/>
    /// <remarks>Writes the hash into <paramref name="destination"/> instead of allocating one, for callers
    /// that only need its bytes.</remarks>
    /// <param name="destination">A 32-byte span, left-padded with zeros when the stored hash is shorter.</param>
    /// <returns><c>true</c> when a hash was written, <c>false</c> when none is available.</returns>
    public bool TryGetBlockHashFromState(BlockHeader currentBlockHeader, ulong requiredBlockNumber, IReleaseSpec spec, Span<byte> destination)
    {
        Hash256? hash = GetBlockHashFromState(currentBlockHeader, requiredBlockNumber, spec);
        if (hash is null) return false;

        hash.Bytes.CopyTo(destination);
        return true;
    }
}
