// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.Evm
{
    public interface IBlockhashProvider
    {
        Hash256? GetBlockhash(BlockHeader currentBlock, ulong number, IReleaseSpec spec);

        /// <summary>Gets the block hash for <paramref name="number"/> as bytes.</summary>
        /// <remarks>Lets the BLOCKHASH opcode push its stack word without materialising a <see cref="Hash256"/>
        /// that it discards on the next instruction. The returned span must point into storage that stays
        /// immutable at least for the caller's frame — never a reusable scratch buffer — and callers must
        /// consume it before any further provider or state call.</remarks>
        /// <param name="hash">The 32 hash bytes, borrowed from the provider rather than copied out.</param>
        /// <returns><c>true</c> when a hash was found, <c>false</c> when none is available.</returns>
        bool TryGetBlockhash(BlockHeader currentBlock, ulong number, IReleaseSpec spec, out ReadOnlySpan<byte> hash)
        {
            Hash256? blockHash = GetBlockhash(currentBlock, number, spec);
            hash = blockHash is null ? default : blockHash.Bytes;
            return blockHash is not null;
        }
        Task Prefetch(BlockHeader currentBlock, CancellationToken token);
    }
}
