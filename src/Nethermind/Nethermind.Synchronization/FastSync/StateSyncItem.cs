// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.Synchronization.FastSync
{
    [DebuggerDisplay("{Level} {NodeDataType} {Hash}")]
    public class StateSyncItem
    {
        public StateSyncItem(Hash256 hash, byte[]? accountPathNibbles, byte[]? pathNibbles, NodeDataType nodeType, int level = 0, uint rightness = 0)
        {
            Hash = hash;
            AccountPathNibbles = accountPathNibbles ?? Array.Empty<byte>();
            PathNibbles = pathNibbles ?? Array.Empty<byte>();
            NodeDataType = nodeType;
            Level = (byte)level;
            Rightness = rightness;
        }

        public StateSyncItem(StateSyncItem original, NodeDataType nodeDataType)
        {
            Hash = original.Hash;
            AccountPathNibbles = original.AccountPathNibbles;
            PathNibbles = original.PathNibbles;
            NodeDataType = nodeDataType;
            Level = original.Level;
            Rightness = original.Rightness;
        }

        public Hash256 Hash { get; }

        /// <summary>
        /// Account part of the path if the item is a Storage node.
        /// It's null when the item is an Account tree node.
        /// </summary>
        public byte[] AccountPathNibbles { get; }

        /// <summary>
        /// Nibbles of item path in the Account tree or a Storage tree.
        /// If item is an Account tree node then <see cref="AccountPathNibbles"/> is null.
        /// </summary>
        public byte[] PathNibbles { get; }

        public NodeDataType NodeDataType { get; }

        public byte Level { get; }

        public short ParentBranchChildIndex { get; set; } = -1;

        public short BranchChildIndex { get; set; } = -1;

        public uint Rightness { get; }

        public bool IsRoot => Level == 0 && NodeDataType == NodeDataType.State;

        public TreePath Path => TreePath.FromNibble(PathNibbles);

        public Hash256? Address => (AccountPathNibbles?.Length ?? 0) != 0 ? new Hash256(Nibbles.ToBytes(AccountPathNibbles)) : null;

        public NodeKey Key
        {
            get
            {
                return new NodeKey(Address, Path, Hash);
            }
        }

        public record NodeKey(Hash256? Address, TreePath? Path, Hash256 Hash);
    }
}
