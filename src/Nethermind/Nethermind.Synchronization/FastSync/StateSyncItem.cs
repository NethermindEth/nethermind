// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Numerics;
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
        public NodeKey Key => _key ??= new(Hash, Address, Path);

        internal readonly struct NodeKeyAsKey(NodeKey Key) : IEquatable<NodeKeyAsKey>
        {
            private readonly NodeKey _key = Key;

            public static implicit operator NodeKeyAsKey(NodeKey key) => new(key);
            public bool Equals(NodeKeyAsKey other) => _key == other._key;
            public override bool Equals(object obj) => obj is NodeKeyAsKey key && Equals(key);
            public override int GetHashCode() => _key.GetHashCode();
        }

        public class NodeKey(Hash256 Hash, Hash256? Address, TreePath? Path) : IEquatable<NodeKey>
        {
            private readonly Hash256? _address = Address;
            private readonly TreePath? _path = Path;
            private readonly Hash256 _hash = Hash;

            public bool Equals(NodeKey? other) => _hash == other._hash && _address == other._address && _path == other._path;
            public override bool Equals(object obj) => Equals(obj as NodeKey);
            public override int GetHashCode() => (int)BitOperations.Crc32C((uint)_hash.GetHashCode(), (ulong)(_path?.GetHashCode() ?? 0) << 32 | (uint)(_address?.GetHashCode() ?? 0));
        }
    }
}
