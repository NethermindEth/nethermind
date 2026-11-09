// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Trie;

/// <summary>
/// Applies the compression that optimizes heavily accounts encoded with RLP that are stored as trie nodes.
/// </summary>
internal sealed class EOACompressingNodeStorage(INodeStorage nodeStorage) : INodeStorage
{
    public INodeStorage.KeyScheme Scheme { get => nodeStorage.Scheme; set => nodeStorage.Scheme = value; }

    public bool RequirePath => nodeStorage.RequirePath;

    public byte[]? Get(Hash256? address, in TreePath path, in ValueHash256 keccak, ReadFlags readFlags = ReadFlags.None)
        => Decompress(nodeStorage.Get(address, in path, in keccak, readFlags));

    public bool TryGet<TArg>(in ValueHash256? address, in TreePath path, in ValueHash256 keccak, ReadFlags readFlags, ReadOnlySpanAction<byte, TArg> action, TArg arg)
        where TArg : allows ref struct =>
        nodeStorage.TryGet(in address, in path, in keccak, readFlags, static (value, read) =>
        {
            if (!IsCompressed(value))
            {
                read.Action(value, read.Arg);
                return;
            }

            // Only EOA accounts are compressed, so the decompressed value fits the stack.
            int length = DecompressedLength(value);
            Span<byte> decompressed = length <= MaxStackDecompressedLength ? stackalloc byte[length] : new byte[length];
            Decompress(value, decompressed);
            read.Action(decompressed, read.Arg);
        }, new SpanActionRead<TArg>(action, arg));

    private const int MaxStackDecompressedLength = 256;

    private readonly ref struct SpanActionRead<TArg>(ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct
    {
        public readonly ReadOnlySpanAction<byte, TArg> Action = action;
        public readonly TArg Arg = arg;
    }

    public void Set(Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> data, WriteFlags writeFlags = WriteFlags.None)
        => nodeStorage.Set(address, in path, in hash, Compress(data, stackalloc byte[data.Length]), writeFlags);

    public INodeStorage.IWriteBatch StartWriteBatch() => new WriteBatch(nodeStorage.StartWriteBatch());

    public bool KeyExists(in ValueHash256? address, in TreePath path, in ValueHash256 hash)
        => nodeStorage.KeyExists(in address, in path, in hash);

    public void Flush(bool onlyWal) => nodeStorage.Flush(onlyWal);

    public void Compact() => nodeStorage.Compact();

    private sealed class WriteBatch(INodeStorage.IWriteBatch writeBatch) : INodeStorage.IWriteBatch
    {
        public void Dispose() => writeBatch.Dispose();

        public void Set(Hash256? address, in TreePath path, in ValueHash256 currentNodeKeccak, ReadOnlySpan<byte> data, WriteFlags writeFlags)
            => writeBatch.Set(address, in path, in currentNodeKeccak, Compress(data, stackalloc byte[data.Length]), writeFlags);
    }

    /// <summary>
    /// The end of rlp of an EOA account, an empty <see cref="Account.CodeHash"/> and an empty <see cref="Account.StorageRoot"/>.
    /// </summary>
    private static ReadOnlySpan<byte> EmptyCodeHashStorageRoot =>
    [
        160, 86, 232, 31, 23, 27, 204, 85, 166, 255, 131, 69, 230, 146, 192, 248, 110, 91, 72, 224, 27, 153,
        108, 173, 192, 1, 98, 47, 181, 227, 99, 180, 33, 160, 197, 210, 70, 1, 134, 247, 35, 60, 146, 126,
        125, 178, 220, 199, 3, 192, 229, 0, 182, 83, 202, 130, 39, 59, 123, 250, 216, 4, 93, 133, 164, 112
    ];

    private const byte PreambleLength = 1;
    private const byte PreambleIndex = 0;
    private const byte PreambleValue = 0;

    private static ReadOnlySpan<byte> Compress(ReadOnlySpan<byte> bytes, Span<byte> compressed)
    {
        if (bytes.IsNull())
            return bytes;

        // no suffix found, return as is
        if (!bytes.EndsWith(EmptyCodeHashStorageRoot))
        {
            return bytes;
        }

        // compression, write [preamble, bytes[0], bytes[1], ...]
        int storedLength = bytes.Length - EmptyCodeHashStorageRoot.Length;
        compressed[PreambleIndex] = PreambleValue;
        bytes[..storedLength].CopyTo(compressed[PreambleLength..]);

        return compressed[..(storedLength + PreambleLength)];
    }

    [return: NotNullIfNotNull(nameof(bytes))]
    private static byte[]? Decompress(byte[]? bytes)
    {
        if (bytes is null || !IsCompressed(bytes))
        {
            return bytes;
        }

        byte[] decompressed = new byte[DecompressedLength(bytes)];
        Decompress(bytes, decompressed);
        return decompressed;
    }

    private static bool IsCompressed(ReadOnlySpan<byte> bytes) => bytes.Length != 0 && bytes[PreambleIndex] == PreambleValue;

    private static int DecompressedLength(ReadOnlySpan<byte> compressed) => compressed.Length - PreambleLength + EmptyCodeHashStorageRoot.Length;

    // decompress, removing preamble and adding the empty at the end
    private static void Decompress(ReadOnlySpan<byte> compressed, Span<byte> decompressed)
    {
        compressed[PreambleLength..].CopyTo(decompressed);
        EmptyCodeHashStorageRoot.CopyTo(decompressed[^EmptyCodeHashStorageRoot.Length..]);
    }
}
