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

        /// <summary>Writes the block hash for <paramref name="number"/> into a 32-byte destination.</summary>
        /// <remarks>Lets the BLOCKHASH opcode fill its stack word without materialising a <see cref="Hash256"/>
        /// that it discards on the next instruction.</remarks>
        /// <param name="destination">A 32-byte span, left-padded with zeros when the hash is shorter.</param>
        /// <returns><c>true</c> when a hash was written, <c>false</c> when none is available.</returns>
        bool TryGetBlockhash(BlockHeader currentBlock, ulong number, IReleaseSpec spec, Span<byte> destination)
        {
            Hash256? hash = GetBlockhash(currentBlock, number, spec);
            if (hash is null) return false;

            hash.Bytes.CopyTo(destination);
            return true;
        }
        Task Prefetch(BlockHeader currentBlock, CancellationToken token);
    }
}
