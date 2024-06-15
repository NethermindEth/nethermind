// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

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

        private TreePath? _treePath = null;
        public TreePath Path => _treePath ??= TreePath.FromNibble(PathNibbles);

        private Hash256? _address = null;
        public Hash256? Address => (AccountPathNibbles?.Length ?? 0) != 0 ? (_address ??= new Hash256(Nibbles.ToBytes(AccountPathNibbles))) : null;

        private NodeKey? _key = null;
        public NodeKey Key => _key ??= new(Address, Path, Hash);

        [StructLayout(LayoutKind.Auto)]
        public readonly struct NodeKey(Hash256? address, TreePath? path, Hash256 hash) : IEquatable<NodeKey>
        {
            private readonly ValueHash256 Address = address ?? default;
            private readonly TreePath? Path = path;
            private readonly ValueHash256 Hash = hash;

            public readonly bool Equals(NodeKey other)
                => Address == other.Address && Path == other.Path && Hash == other.Hash;

            public override bool Equals(object obj)
                => obj is NodeKey && Equals((NodeKey)obj);

            public override int GetHashCode()
            {
                uint hash0 = (uint)hash.GetHashCode();
                ulong hash1 = ((ulong)(uint)(address?.GetHashCode() ?? 1) << 32) | (ulong)(uint)(Path?.GetHashCode() ?? 2);
                return (int)BitOperations.Crc32C(hash0, hash1);
            }

            public static bool operator ==(in NodeKey left, in NodeKey right)
            {
                return left.Equals(right);
            }

            public static bool operator !=(in NodeKey left, in NodeKey right)
            {
                return !(left == right);
            }
        }
    }
}
