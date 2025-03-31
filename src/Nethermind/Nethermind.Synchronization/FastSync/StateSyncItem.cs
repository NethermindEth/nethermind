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
    public class StateSyncItem(
        Hash256 hash,
        Hash256? address,
        TreePath path,
        NodeDataType nodeType,
        int level = 0,
        uint rightness = 0)
    {
        public Hash256 Hash { get; } = hash;

        /// <summary>
        /// Account part of the path if the item is a Storage node.
        /// It's null when the item is an Account tree node.
        /// </summary>
        public Hash256? Address { get; } = address;

        /// <summary>
        /// Nibbles of item path in the Account tree or a Storage tree.
        /// If item is an Account tree node then <see cref="Address"/> is null.
        /// </summary>
        public TreePath Path { get; } = path;

        public NodeDataType NodeDataType { get; } = nodeType;

        public byte Level { get; } = (byte)level;

        public short ParentBranchChildIndex { get; set; } = -1;

        public short BranchChildIndex { get; set; } = -1;

        public uint Rightness { get; } = rightness;

        public bool IsRoot => Level == 0 && NodeDataType == NodeDataType.State;

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
                => obj is NodeKey key && Equals(key);

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
