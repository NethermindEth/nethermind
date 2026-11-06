// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
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

    [SkipLocalsInit]
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

        [SkipLocalsInit]
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
        if (bytes is null || bytes.Length == 0 || (bytes[PreambleIndex] != PreambleValue))
        {
            return bytes;
        }

        // decompress, removing preamble and adding the empty at the end
        byte[] decompressed = new byte[bytes.Length - PreambleLength + EmptyCodeHashStorageRoot.Length];
        Span<byte> span = decompressed.AsSpan();

        bytes.AsSpan(PreambleLength).CopyTo(span);
        EmptyCodeHashStorageRoot.CopyTo(span[^EmptyCodeHashStorageRoot.Length..]);

        return decompressed;
    }
}
