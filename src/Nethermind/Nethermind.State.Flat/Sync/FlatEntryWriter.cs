// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.Sync;

/// <summary>
/// Utility for writing flat entries during sync. Handles both direct leaf nodes
/// and inline leaf children (nodes with RLP &lt; 32 bytes embedded in parent).
/// </summary>
internal static class FlatEntryWriter
{
    /// <summary>
    /// Write flat account entries for a node and any inline leaf children.
    /// </summary>
    public static void WriteAccountFlatEntries(
        IPersistence.IWriteBatch writeBatch,
        ref TreePath path,
        TrieNode node)
    {
        if (node.IsLeaf)
        {
            ValueHash256 fullPath = path.Append(node.Key).Path;
            Account account = AccountDecoder.Instance.Decode(node.Value.Span)!;
            writeBatch.SetAccountRaw(fullPath.ToCommitment(), account);
            return;
        }

        if (node.IsBranch)
        {
            BranchInlineChildLeafEnumerator enumerator = new(ref path, node);
            while (enumerator.MoveNext())
            {
                Account account = AccountDecoder.Instance.Decode(enumerator.CurrentValue)!;
                writeBatch.SetAccountRaw(enumerator.CurrentPath.ToCommitment(), account);
            }
        }
        else if (node.IsExtension)
        {
            ExtensionInlineChildLeafEnumerator enumerator = new(ref path, node);
            while (enumerator.MoveNext())
            {
                Account account = AccountDecoder.Instance.Decode(enumerator.CurrentValue)!;
                writeBatch.SetAccountRaw(enumerator.CurrentPath.ToCommitment(), account);
            }
        }
    }

    /// <summary>
    /// Write flat storage entries for a node and any inline leaf children.
    /// </summary>
    public static void WriteStorageFlatEntries(
        IPersistence.IWriteBatch writeBatch,
        Hash256 address,
        TreePath path,
        TrieNode node)
    {
        if (node.IsLeaf)
        {
            ValueHash256 fullPath = path.Append(node.Key).Path;
            WriteStorageLeaf(writeBatch, address, fullPath, node.Value.Span);
            return;
        }

        if (node.IsBranch)
        {
            BranchInlineChildLeafEnumerator enumerator = new(ref path, node);
            while (enumerator.MoveNext())
            {
                WriteStorageLeaf(writeBatch, address, enumerator.CurrentPath, enumerator.CurrentValue);
            }
        }
        else if (node.IsExtension)
        {
            ExtensionInlineChildLeafEnumerator enumerator = new(ref path, node);
            while (enumerator.MoveNext())
            {
                WriteStorageLeaf(writeBatch, address, enumerator.CurrentPath, enumerator.CurrentValue);
            }
        }
    }

    private static void WriteStorageLeaf(
        IPersistence.IWriteBatch writeBatch,
        Hash256 address,
        ValueHash256 fullPath,
        ReadOnlySpan<byte> value)
    {
        byte[] toWrite = value.IsEmpty
            ? State.StorageTree.ZeroBytes
            : value.AsRlpValueContext().DecodeByteArray();
        writeBatch.SetStorageRaw(address, fullPath.ToCommitment(), SlotValue.FromSpanWithoutLeadingZero(toWrite));
    }

    /// <summary>
    /// High-performance enumerator for inline leaf children of a branch node.
    /// Operates directly on RLP data to avoid TrieNode wrapper allocations.
    /// Iterates 16 children, yielding only inline leaves.
    /// </summary>
    public ref struct BranchInlineChildLeafEnumerator
    {
        private readonly ReadOnlySpan<byte> _rlp;
        private readonly int _originalPathLength;
        private ref TreePath _path;
        private int _index;
        private int _rlpPosition;

        private ValueHash256 _currentFullPath;
        private ReadOnlySpan<byte> _currentValue;
        private ReadOnlySpan<byte> _currentRlp;

        public BranchInlineChildLeafEnumerator(ref TreePath path, TrieNode node)
        {
            _path = ref path;
            _rlp = node.FullRlp.Span;
            _originalPathLength = path.Length;
            _index = -1;
            _currentFullPath = default;
            _currentValue = default;
            _currentRlp = default;

            // Skip list prefix to position at first child
            Rlp.ValueDecoderContext ctx = new(_rlp);
            ctx.SkipLength();
            _rlpPosition = ctx.Position;
        }

        public ValueHash256 CurrentPath => _currentFullPath;
        public TreePath IntermediatePath => _path;
        public ReadOnlySpan<byte> CurrentValue => _currentValue;

        /// <summary>
        /// Creates a TrieNode from the current inline leaf RLP.
        /// Use this when you need the full TrieNode object (e.g., for deletion range computation).
        /// </summary>
        public TrieNode CurrentNode
        {
            get
            {
                TrieNode node = new(NodeType.Unknown, _currentRlp.ToArray());
                node.ResolveNode(NullTrieNodeResolver.Instance, _path);
                return node;
            }
        }

        public bool MoveNext()
        {
            _path.TruncateMut(_originalPathLength);

            while (++_index < 16)
            {
                Rlp.ValueDecoderContext ctx = new(_rlp) { Position = _rlpPosition };

                int prefix = ctx.ReadByte();

                switch (prefix)
                {
                    case 0:
                    case 128: // Empty/null child (0x80)
                        _rlpPosition = ctx.Position;
                        continue;

                    case 160: // Hash reference (0xa0 = 32-byte Keccak)
                        ctx.Position--;
                        ctx.SkipItem();
                        _rlpPosition = ctx.Position;
                        continue;

                    default: // Inline node
                        ctx.Position--;
                        int length = ctx.PeekNextRlpLength();
                        ReadOnlySpan<byte> inlineRlp = ctx.PeekNextItem();

                        if (TryExtractLeafData(inlineRlp, out ReadOnlySpan<byte> currentKey, out _currentValue))
                        {
                            _currentRlp = inlineRlp;
                            _path.AppendMut(_index);
                            _currentFullPath = _path.Append(currentKey).Path;
                            _rlpPosition = ctx.Position + length;
                            return true;
                        }

                        _rlpPosition = ctx.Position;
                        continue;
                }
            }

            return false;
        }

    }

    /// <summary>
    /// High-performance enumerator for inline leaf child of an extension node.
    /// Operates directly on RLP data to avoid TrieNode wrapper allocations.
    /// Extension has exactly one child at path + Key.
    /// </summary>
    public ref struct ExtensionInlineChildLeafEnumerator
    {
        private readonly ReadOnlySpan<byte> _rlp;
        private readonly int _originalPathLength;
        private ref TreePath _path;
        private bool _done;

        private readonly ReadOnlySpan<byte> _extensionKey;

        private ValueHash256 _currentFullPath;
        private ReadOnlySpan<byte> _currentValue;
        private ReadOnlySpan<byte> _currentRlp;

        public ExtensionInlineChildLeafEnumerator(ref TreePath path, TrieNode node)
        {
            _path = ref path;
            _rlp = node.FullRlp.Span;
            _originalPathLength = path.Length;
            _done = false;
            _currentFullPath = default;
            _currentValue = default;
            _currentRlp = default;

            // Extract extension key for path building
            Rlp.ValueDecoderContext ctx = new(_rlp);
            ctx.ReadSequenceLength();
            _extensionKey = ctx.DecodeByteArraySpan();
        }

        public ValueHash256 CurrentPath => _currentFullPath;
        public TreePath IntermediatePath => _path;
        public ReadOnlySpan<byte> CurrentValue => _currentValue;

        /// <summary>
        /// Creates a TrieNode from the current inline leaf RLP.
        /// Use this when you need the full TrieNode object (e.g., for deletion range computation).
        /// </summary>
        public TrieNode CurrentNode
        {
            get
            {
                TrieNode node = new(NodeType.Unknown, _currentRlp.ToArray());
                node.ResolveNode(NullTrieNodeResolver.Instance, _path);
                return node;
            }
        }

        public bool MoveNext()
        {
            if (_done)
            {
                _path.TruncateMut(_originalPathLength);
                return false;
            }

            _done = true;

            // Position at child (index 1)
            Rlp.ValueDecoderContext ctx = new(_rlp);
            ctx.ReadSequenceLength();
            ctx.SkipItem(); // Skip key

            int prefix = ctx.ReadByte();


            switch (prefix)
            {
                case 0:
                case 128: // Empty - should not happen for valid extension
                case 160: // Hash reference - not inline
                    _path.TruncateMut(_originalPathLength);
                    return false;

                default: // Inline node
                    ctx.Position--;
                    ReadOnlySpan<byte> inlineRlp = ctx.PeekNextItem();

                    if (TryExtractLeafData(inlineRlp, out ReadOnlySpan<byte> currentKey, out _currentValue))
                    {
                        _currentRlp = inlineRlp;
                        // Append extension key nibbles to path
                        (byte[] extensionKeyNibbles, _) = HexPrefix.FromBytes(_extensionKey);
                        _path.AppendMut(extensionKeyNibbles);
                        _currentFullPath = _path.Append(currentKey).Path;
                        return true;
                    }

                    _path.TruncateMut(_originalPathLength);
                    return false;
            }
        }
    }

    private static bool TryExtractLeafData(
        ReadOnlySpan<byte> nodeRlp,
        out ReadOnlySpan<byte> key,
        out ReadOnlySpan<byte> value)
    {
        Rlp.ValueDecoderContext ctx = new(nodeRlp);
        ctx.ReadSequenceLength();

        ReadOnlySpan<byte> keySpan = ctx.DecodeByteArraySpan();
        (byte[] keyBytes, bool isLeaf) = HexPrefix.FromBytes(keySpan);

        // Check if leaf (0x20 bit set in first nibble)
        if (isLeaf)
        {
            value = ctx.DecodeByteArraySpan();
            key = keyBytes;
            return true;
        }

        key = default;
        value = default;
        return false;
    }

    /// <summary>
    /// Minimal resolver that throws on all operations.
    /// Safe to use for inline nodes since they have embedded RLP and don't need resolution.
    /// </summary>
    internal sealed class NullTrieNodeResolver : ITrieNodeResolver
    {
        public static readonly NullTrieNodeResolver Instance = new();
        public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => new TrieNode(NodeType.Unknown, hash);
        public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => throw new NotSupportedException();
        public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => throw new NotSupportedException();
        public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) => throw new NotSupportedException();
        public INodeStorage.KeyScheme Scheme => INodeStorage.KeyScheme.HalfPath;
    }
}
