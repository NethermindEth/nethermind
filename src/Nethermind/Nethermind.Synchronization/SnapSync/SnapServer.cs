// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;
using Nethermind.State.SnapServer;

namespace Nethermind.Synchronization.SnapSync;

public sealed class SnapServer(
    ISnapStateServer stateServer,
    [KeyFilter(DbNames.Code)] IReadOnlyKeyValueStore codeDb,
    IBlockTree blockTree,
    IBlockAccessListStore blockAccessListStore) : ISnapServer
{
    private const long HardResponseByteLimit = ISnapStateServer.HardResponseByteLimit;
    private const int CodeReadChunkSize = 64;

    public bool CanServe => stateServer.CanServe;

    public IByteArrayList? GetTrieNodes(IReadOnlyList<PathGroup> pathSet, Hash256 rootHash, CancellationToken cancellationToken) =>
        stateServer.GetTrieNodes(pathSet, rootHash, cancellationToken);

    public IByteArrayList? GetTrieNodes(IReadOnlyList<PathGroup> pathSet, Hash256 rootHash, long byteLimit, CancellationToken cancellationToken) =>
        stateServer.GetTrieNodes(pathSet, rootHash, byteLimit, cancellationToken);

    public (IOwnedReadOnlyList<PathWithAccount>, IByteArrayList) GetAccountRanges(Hash256 rootHash,
        in ValueHash256 startingHash,
        in ValueHash256? limitHash,
        long byteLimit,
        CancellationToken cancellationToken) =>
        stateServer.GetAccountRanges(rootHash, startingHash, limitHash, byteLimit, cancellationToken);

    public (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>>, IByteArrayList?) GetStorageRanges(
        Hash256 rootHash,
        IReadOnlyList<PathWithAccount> accounts,
        in ValueHash256? startingHash,
        in ValueHash256? limitHash,
        long byteLimit,
        CancellationToken cancellationToken) =>
        stateServer.GetStorageRanges(rootHash, accounts, startingHash, limitHash, byteLimit, cancellationToken);

    public IByteArrayList GetByteCodes(IReadOnlyList<ValueHash256> requestedHashes, long byteLimit, CancellationToken cancellationToken)
    {
        if (byteLimit > HardResponseByteLimit)
            byteLimit = HardResponseByteLimit;

        using DeferredRlpItemList.Builder builder = new(requestedHashes.Count);
        DeferredRlpItemList.Builder.Writer writer = builder.BeginRootContainer();
        CodeResponseWriter codes = new(builder, byteLimit, cancellationToken);
        Span<byte> keys = stackalloc byte[CodeReadChunkSize * ValueHash256.MemorySize];
        int pending = 0;

        foreach (ValueHash256 codeHash in requestedHashes)
        {
            if (codeHash.Bytes.SequenceEqual(Keccak.OfAnEmptyString.Bytes))
            {
                ReadCodes(keys[..(pending * ValueHash256.MemorySize)], codes);
                pending = 0;
                if (codes.IsFull) break;

                codes.WriteEmpty();
                continue;
            }

            codeHash.Bytes.CopyTo(keys[(pending * ValueHash256.MemorySize)..]);
            if (++pending < CodeReadChunkSize) continue;

            ReadCodes(keys, codes);
            pending = 0;
            if (codes.IsFull) break;
        }

        ReadCodes(keys[..(pending * ValueHash256.MemorySize)], codes);
        writer.Dispose();
        return new RlpByteArrayList(builder.ToRlpItemList());
    }

    /// <summary>Writes the stored codes for <paramref name="keys"/> in order, skipping missing ones.</summary>
    /// <remarks>
    /// A batch reads the whole chunk even past the byte limit, which <see cref="CodeReadChunkSize"/> bounds.
    /// Code keys are hashes, so a cached block holds no likely next request: the reads hint a cache miss.
    /// </remarks>
    private void ReadCodes(ReadOnlySpan<byte> keys, CodeResponseWriter codes)
    {
        int count = keys.Length / ValueHash256.MemorySize;
        if (count == 0 || codes.IsFull) return;

        if (codeDb is IBatchReadOnlyDb batch)
        {
            Span<bool> found = stackalloc bool[count];
            batch.GetMany(keys, ValueHash256.MemorySize, found, codes, ReadFlags.HintCacheMiss);
            return;
        }

        for (int i = 0; i < count && !codes.IsFull; i++)
        {
            Span<byte> code = codeDb.GetSpan(keys.Slice(i * ValueHash256.MemorySize, ValueHash256.MemorySize), ReadFlags.HintCacheMiss);
            try
            {
                if (!code.IsNull()) codes.Decode(code);
            }
            finally
            {
                codeDb.DangerousReleaseMemory(code);
            }
        }
    }

    /// <summary>Appends served codes to the response until the byte limit is passed or the request is cancelled.</summary>
    private sealed class CodeResponseWriter(DeferredRlpItemList.Builder builder, long byteLimit, CancellationToken cancellationToken)
        : IValueDecoder<bool>
    {
        private long _byteCount;

        public bool IsFull => _byteCount > byteLimit || cancellationToken.IsCancellationRequested;

        /// <remarks>A root writer only appends to the root entry, so a fresh one writes into the same list.</remarks>
        public bool Decode(ReadOnlySpan<byte> code)
        {
            if (IsFull) return false;

            builder.BeginRootContainer().WriteValue(code);
            _byteCount += code.Length;
            return true;
        }

        public void WriteEmpty()
        {
            builder.BeginRootContainer().WriteValue([]);
            _byteCount += 1;
        }
    }

    public IByteArrayList GetBlockAccessLists(IReadOnlyList<ValueHash256> blockHashes, long byteLimit, CancellationToken cancellationToken)
    {
        if (byteLimit > HardResponseByteLimit)
            byteLimit = HardResponseByteLimit;

        long currentByteCount = 0;
        using DeferredRlpItemList.Builder builder = new(blockHashes.Count);
        DeferredRlpItemList.Builder.Writer writer = builder.BeginRootContainer();

        foreach (ValueHash256 blockHash in blockHashes)
        {
            if (currentByteCount > byteLimit || cancellationToken.IsCancellationRequested) break;

            Hash256 hash = blockHash.ToCommitment();
            BlockHeader? header = blockTree.FindHeader(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            using MemoryManager<byte>? balRlp = header?.BlockAccessListHash is null
                ? null
                : blockAccessListStore.GetRlp(header.Number, hash);

            if (balRlp is null)
            {
                writer.WriteValue([]);
                currentByteCount += 1;
                continue;
            }

            ReadOnlySpan<byte> span = balRlp.Memory.Span;
            writer.WriteValue(span);
            currentByteCount += span.Length;
        }

        writer.Dispose();
        return new RlpByteArrayList(builder.ToRlpItemList());
    }
}
