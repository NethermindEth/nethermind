// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain
{
    public interface IBlockTreeHealer
    {
        /// <summary>
        /// Walks backward from <paramref name="startHash"/> up to <paramref name="maxBlockDepth"/> blocks,
        /// repairing incorrect <c>HasBlockOnMainChain</c> markers. Also removes stale canonical markers
        /// above <paramref name="startHash"/> left by the beacon-sync path.
        /// </summary>
        void HealCanonicalChain(Hash256 startHash, long maxBlockDepth);
    }
}
