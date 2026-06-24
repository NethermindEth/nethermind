// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.Evm
{
    public interface IBlockhashProvider
    {
        Hash256? GetBlockhash(BlockHeader currentBlock, long number, IReleaseSpec spec);

        /// <summary>
        /// Resolves the BLOCKHASH value for <paramref name="number"/> without throwing when it cannot be resolved.
        /// </summary>
        /// <param name="hash">
        /// The resolved hash. May be <c>null</c> even on success for out-of-window blocks, for which the EVM pushes 0.
        /// </param>
        /// <returns>
        /// <c>true</c> when resolution completed (including the valid out-of-window case);
        /// <c>false</c> when the hash is genuinely unavailable in the current context.
        /// </returns>
        bool TryGetBlockhash(BlockHeader currentBlock, long number, IReleaseSpec spec, out Hash256? hash)
        {
            hash = GetBlockhash(currentBlock, number, spec);
            return true;
        }

        Task Prefetch(BlockHeader currentBlock, CancellationToken token);
    }
}
