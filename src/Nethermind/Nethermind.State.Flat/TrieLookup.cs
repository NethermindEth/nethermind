// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;

namespace Nethermind.State.Flat;

/// <summary>The walk to one key of a trie, advanced by the borrowed RLP of each node read on the way.</summary>
/// <param name="isStorage">Whether the key is a slot, whose value is decoded to <see cref="Slot"/>, rather than an account.</param>
internal struct TrieLookup(in ValueHash256 key, bool isStorage)
{
    private const int KeyNibbles = 64;
    private readonly TreePath _key = new(key, KeyNibbles);

    /// <summary>The path of the next node to read, until <see cref="IsDone"/>.</summary>
    public TreePath NodePath;
    /// <summary>The hash of the next node to read, until <see cref="IsDone"/>.</summary>
    public ValueHash256 NodeHash;
    public bool IsDone;
    public bool Found;
    public Account? Account;
    public UInt256 Slot;

    /// <summary>Follows the key through <paramref name="node"/> and the nodes embedded in it, to the next node to read or the end of the walk.</summary>
    public void Descend(ReadOnlySpan<byte> node)
    {
        Span<byte> nibbles = stackalloc byte[KeyNibbles];
        while (true)
        {
            RlpReader reader = new(node);
            reader.ReadSequenceLength();
            if (reader.PeekNumberOfItemsRemaining(maxSearch: 3) > 2)
            {
                int nibble = _key[NodePath.Length];
                reader.SkipItems(nibble);
                NodePath.AppendMut(nibble);
            }
            else
            {
                ReadOnlySpan<byte> hexPrefix = reader.DecodeByteArraySpan();
                int length = NodeRlp.HexPrefixNibbleCount(hexPrefix);
                if (length > KeyNibbles - NodePath.Length)
                {
                    IsDone = true;
                    return;
                }

                NodeRlp.ReadHexPrefix(hexPrefix, 0, nibbles);
                TreePath childPath = NodePath.Append(nibbles[..length]);
                if (NodeRlp.IsLeafHexPrefix(hexPrefix))
                {
                    if (childPath == _key) Decode(reader.DecodeByteArraySpan());
                    IsDone = true;
                    return;
                }

                if (!_key.StartsWith(childPath))
                {
                    IsDone = true;
                    return;
                }

                NodePath = childPath;
            }

            if (reader.IsSequenceNext())
            {
                node = reader.PeekNextItem();
                continue;
            }

            ReadOnlySpan<byte> childHash = reader.DecodeByteArraySpan();
            if (childHash.IsEmpty) IsDone = true;
            else NodeHash = new ValueHash256(childHash);
            return;
        }
    }

    private void Decode(ReadOnlySpan<byte> value)
    {
        Found = true;
        RlpReader reader = new(value);
        if (isStorage) Slot = new UInt256(reader.DecodeByteArraySpan(), isBigEndian: true);
        else Account = AccountDecoder.Instance.Decode(ref reader);
    }
}
