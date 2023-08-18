// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public interface ITrieNodeResolver
    {
        /// <summary>
        /// Returns a cached and resolved <see cref="TrieNode"/> or a <see cref="TrieNode"/> with Unknown type
        /// but the hash set. The latter case allows to resolve the node later. Resolving the node means loading
        /// its RLP data from the state database.
        /// </summary>
        /// <param name="hash">Keccak hash of the RLP of the node.</param>
        /// <returns></returns>
        TrieNode FindCachedOrUnknown(Keccak hash);

        /// <summary>
        /// Loads RLP of the node.
        /// </summary>
        /// <param name="hash"></param>
        /// <returns></returns>
        byte[]? LoadRlp(Keccak hash, ReadFlags flags = ReadFlags.None);
    }

    public static class IBufferPoolExtensions
    {
        public static CappedArray<byte> SafeRentBuffer(this IBufferPool? pool, int size)
        {
            if (pool == null) return new CappedArray<byte>(new byte[size]);
            CappedArray<byte> returnedBuffer = pool.RentBuffer(size);
            if (returnedBuffer.Array == null)
            {
                return new CappedArray<byte>(new byte[size]);
            }

            return returnedBuffer;
        }

        public static void SafeReturnBuffer(this IBufferPool? pool, CappedArray<byte> buffer)
        {
            pool?.ReturnBuffer(buffer);
        }
    }

    public interface IBufferPool
    {
        CappedArray<byte> RentBuffer(int size) => new CappedArray<byte>(new byte[size]);

        void ReturnBuffer(CappedArray<byte> buffer)
        {
        }
    }
}
