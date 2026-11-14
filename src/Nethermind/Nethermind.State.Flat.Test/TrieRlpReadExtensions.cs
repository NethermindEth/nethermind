// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Test;

/// <summary>Test-only reads of a trie node's RLP as an owned copy, through the span-action reads.</summary>
internal static class TrieRlpReadExtensions
{
    public static byte[]? LoadStateRlp(this IPersistence.IPersistenceReader reader, in TreePath path, ReadFlags flags)
    {
        byte[]? rlp = null;
        reader.TryLoadStateRlp(path, flags, static (value, result) => result[0] = value.ToArray(), new Span<byte[]?>(ref rlp));
        return rlp;
    }

    public static byte[]? LoadStorageRlp(this IPersistence.IPersistenceReader reader, Hash256 address, in TreePath path, ReadFlags flags)
    {
        byte[]? rlp = null;
        reader.TryLoadStorageRlp(address, path, flags, static (value, result) => result[0] = value.ToArray(), new Span<byte[]?>(ref rlp));
        return rlp;
    }

    public static byte[]? LoadStateRlp<TReader>(this TReader reader, in TreePath path, ReadFlags flags) where TReader : BasePersistence.ITrieReader
    {
        byte[]? rlp = null;
        reader.TryLoadStateRlp(path, flags, static (value, result) => result[0] = value.ToArray(), new Span<byte[]?>(ref rlp));
        return rlp;
    }

    public static byte[]? LoadStorageRlp<TReader>(this TReader reader, Hash256 address, in TreePath path, ReadFlags flags) where TReader : BasePersistence.ITrieReader
    {
        byte[]? rlp = null;
        reader.TryLoadStorageRlp(address, path, flags, static (value, result) => result[0] = value.ToArray(), new Span<byte[]?>(ref rlp));
        return rlp;
    }
}
