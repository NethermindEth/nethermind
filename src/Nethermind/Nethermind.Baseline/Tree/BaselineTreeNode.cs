// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Baseline.Tree
{
    public readonly struct BaselineTreeNode
    {
        public BaselineTreeNode(Keccak hash, ulong nodeIndex)
        {
            Hash = hash;
            NodeIndex = nodeIndex;
        }

        /// <summary>
        /// Keccak here in order not to add a new converter at the moment
        /// </summary>
        public Keccak Hash { get; }
        public ulong NodeIndex { get; } // 64bit index for a tree of height 32

        public override string ToString()
        {
            return $"{NodeIndex}.{Hash}";
        }
    }
}
